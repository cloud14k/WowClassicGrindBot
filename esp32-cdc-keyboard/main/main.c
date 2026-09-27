#include "esp_log.h"
#include "tinyusb.h"
#include "tusb_cdc_acm.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "freertos/queue.h"
#include <string.h>
#include <stdio.h>
#include <stdarg.h>

static const char *TAG = "hid_bridge";
extern tinyusb_config_t tusb_cfg;
static uint8_t rx[64];
static size_t rx_len;
static QueueHandle_t reports;
typedef struct { uint8_t id, sequence, size, payload[8]; } report_t;

static void acknowledge(uint8_t sequence, uint8_t status)
{
    uint8_t ack[] = {0x5A, sequence, status};
    tinyusb_cdcacm_write_queue(TINYUSB_CDC_ACM_0, ack, sizeof(ack));
    tinyusb_cdcacm_write_flush(TINYUSB_CDC_ACM_0, 0);
}

static void uart_log(uint8_t sequence, const char *format, ...)
{
    char line[132];
    va_list args;
    va_start(args, format);
    int length = snprintf(line, sizeof(line), "[%u]", sequence);
    if (length > 0 && length < sizeof(line))
        length += vsnprintf(line + length, sizeof(line) - length, format, args);
    va_end(args);
    if (length > 0) {
        size_t size = (size_t)length < sizeof(line) ? (size_t)length : sizeof(line) - 1;
        line[size] = '\0';
        ESP_LOGI(TAG, "%s", line);
    }
}

static const char *key_name(uint8_t usage)
{
    static char name[8];
    if (usage >= 0x04 && usage <= 0x1D) { snprintf(name, sizeof(name), "%c", 'A' + usage - 4); return name; }
    if (usage >= 0x1E && usage <= 0x27) { snprintf(name, sizeof(name), "%c", usage == 0x27 ? '0' : '1' + usage - 0x1E); return name; }
    switch (usage) {
        case 0x28: return "ENTER"; case 0x29: return "ESC"; case 0x2A: return "BACKSPACE";
        case 0x2B: return "TAB"; case 0x2C: return "SPACE"; case 0x4A: return "HOME";
        case 0x4B: return "PAGEUP"; case 0x4E: return "PAGEDOWN";
        default: snprintf(name, sizeof(name), "0x%02X", usage); return name;
    }
}

static void report_events(const report_t *report, const uint8_t *old_keyboard, uint8_t old_buttons)
{
    if (report->id == 1) {
        const uint8_t *now = report->payload;
        static const char *mods[] = {"LCTRL", "LSHIFT", "LALT", "LGUI", "RCTRL", "RSHIFT", "RALT", "RGUI"};
        uart_log(report->sequence, "[RX] KEYBOARD REPORT");
        for (int i = 0; i < 8; ++i) if (((old_keyboard[0] ^ now[0]) & (1 << i)) != 0)
            uart_log(report->sequence, "[HID] %s %s", mods[i], (now[0] & (1 << i)) ? "DOWN" : "UP");
        for (int i = 2; i < 8; ++i) if (old_keyboard[i] && memchr(now + 2, old_keyboard[i], 6) == NULL)
            uart_log(report->sequence, "[HID] %s UP", key_name(old_keyboard[i]));
        for (int i = 2; i < 8; ++i) if (now[i] && memchr(old_keyboard + 2, now[i], 6) == NULL)
            uart_log(report->sequence, "[HID] %s DOWN", key_name(now[i]));
    } else {
        const uint8_t *now = report->payload;
        uart_log(report->sequence, "[RX] MOUSE REPORT");
        static const char *button_names[] = {"LEFT", "RIGHT", "MIDDLE", "BUTTON4", "BUTTON5"};
        for (int i = 0; i < 5; ++i) if (((old_buttons ^ now[0]) & (1 << i)) != 0)
            uart_log(report->sequence, "[HID] MOUSE_%s %s", button_names[i], (now[0] & (1 << i)) ? "DOWN" : "UP");
        int8_t x = (int8_t)now[1], y = (int8_t)now[2], wheel = (int8_t)now[3];
        if (x || y) uart_log(report->sequence, "[HID] MOUSE_MOVE %d %d", x, y);
        if (wheel) uart_log(report->sequence, "[HID] WHEEL %d", wheel);
    }
}

static void consume(void)
{
    while (rx_len >= 5) {
        if (rx[0] != 0xA5) { memmove(rx, rx + 1, --rx_len); continue; }
        uint8_t size = rx[3];
        if (size > 8 || (rx[1] == 1 && size != 8) || (rx[1] == 2 && size != 5) || (rx[1] != 1 && rx[1] != 2)) {
            memmove(rx, rx + 1, --rx_len); continue;
        }
        size_t frame_len = size + 5;
        if (rx_len < frame_len) return;
        uint8_t checksum = 0;
        for (size_t i = 0; i < frame_len; ++i) checksum ^= rx[i];
        if (checksum == 0) {
            report_t report = {.id = rx[1], .sequence = rx[2], .size = size};
            memcpy(report.payload, rx + 4, size);
            if (xQueueSend(reports, &report, 0) != pdTRUE)
                ESP_LOGW(TAG, "HID report queue full; retry sequence %u", report.sequence);
            memmove(rx, rx + frame_len, rx_len - frame_len);
            rx_len -= frame_len;
        } else memmove(rx, rx + 1, --rx_len);
    }
}

void tinyusb_cdc_rx_callback(int itf, cdcacm_event_t *event)
{
    uint8_t chunk[64]; size_t count = 0;
    (void)event;
    while (tinyusb_cdcacm_read(itf, chunk, sizeof(chunk), &count) == ESP_OK && count) {
        for (size_t i = 0; i < count; ++i) {
            if (rx_len == sizeof(rx)) rx_len = 0;
            rx[rx_len++] = chunk[i];
            consume();
        }
    }
}

static void send_task(void *arg)
{
    report_t report; (void)arg;
    uint8_t last_sequence = 0;
    bool has_last = false;
    uint8_t keyboard_state[8] = {0};
    uint8_t mouse_buttons = 0;
    for (;;) {
        if (xQueueReceive(reports, &report, portMAX_DELAY) != pdTRUE) continue;
        if (has_last && report.sequence == last_sequence) { acknowledge(report.sequence, 0); continue; }
        while (!tud_hid_ready()) vTaskDelay(pdMS_TO_TICKS(1));
        if (tud_hid_n_report(0, report.id, report.payload, report.size)) {
            report_events(&report, keyboard_state, mouse_buttons);
            if (report.id == 1) memcpy(keyboard_state, report.payload, sizeof(keyboard_state));
            else mouse_buttons = report.payload[0];
            last_sequence = report.sequence;
            has_last = true;
            acknowledge(report.sequence, 0);
            uart_log(report.sequence, "[DONE] OK");
        } else {
            acknowledge(report.sequence, 1);
            uart_log(report.sequence, "[DONE] FAIL");
        }
    }
}

void app_main(void)
{
    reports = xQueueCreate(32, sizeof(report_t));
    ESP_ERROR_CHECK(tinyusb_driver_install(&tusb_cfg));
    tinyusb_config_cdcacm_t acm_cfg = {
        .usb_dev = TINYUSB_USBDEV_0,
        .cdc_port = TINYUSB_CDC_ACM_0,
        .rx_unread_buf_sz = 64,
        .callback_rx = &tinyusb_cdc_rx_callback,
        .callback_rx_wanted_char = NULL,
    };
    ESP_ERROR_CHECK(tusb_cdc_acm_init(&acm_cfg));
    xTaskCreate(send_task, "hid_send", 4096, NULL, 5, NULL);
    ESP_LOGI(TAG, "Framed CDC HID bridge ready");
}
