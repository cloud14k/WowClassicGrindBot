using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;

namespace HidShared;

/// <summary>Serial reader for framed HID commands and binary report ACKs.</summary>
public sealed class SerialClient : IAsyncDisposable
{
    private readonly SerialPort port;
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim writer = new(1, 1);
    private readonly Dictionary<byte, TaskCompletionSource<bool>> pending = new();
    private readonly object pendingLock = new();
    private readonly Task readTask;
    private byte sequence;
    private bool disposed;
    private Exception? connectionFailure;

    public event Action<Exception>? ConnectionLost;
    public string PortName => port.PortName;
    public bool IsOpen => !disposed && Volatile.Read(ref connectionFailure) is null && port.IsOpen;

    public SerialClient(string portName, int baudRate = 115200)
    {
        port = new SerialPort(portName, baudRate) { ReadTimeout = 1000, WriteTimeout = 1000, NewLine = "\n" };
        port.Open();
        readTask = Task.Run(ReadLoopAsync);
    }

    public async Task<byte> SendReportAsync(byte reportId, ReadOnlyMemory<byte> payload, CancellationToken token = default)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(token, stop.Token);
        await writer.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref connectionFailure) is Exception failure)
                throw new IOException("CDC serial connection was lost.", failure);
            byte id = unchecked(++sequence);
            byte[] frame = HidProtocol.Encode(reportId, id, payload.Span);
            for (int attempt = 0; attempt < 3; attempt++)
            {
                var ack = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (pendingLock)
                {
                    if (Volatile.Read(ref connectionFailure) is Exception readFailure)
                        throw new IOException("CDC serial connection was lost.", readFailure);
                    pending[id] = ack;
                }
                try
                {
                    await port.BaseStream.WriteAsync(frame, linked.Token).ConfigureAwait(false);
                    try
                    {
                        bool accepted = await ack.Task.WaitAsync(TimeSpan.FromSeconds(2), linked.Token).ConfigureAwait(false);
                        if (accepted) return id;
                    }
                    catch (TimeoutException) when (attempt < 2) { }
                }
                finally { lock (pendingLock) pending.Remove(id); }
            }
            throw new IOException($"S3 did not acknowledge report {id}.");
        }
        finally { writer.Release(); }
    }

    private async Task ReadLoopAsync()
    {
        byte[] one = new byte[1];
        try
        {
            while (!stop.IsCancellationRequested)
            {
                int count = await port.BaseStream.ReadAsync(one, stop.Token).ConfigureAwait(false);
                if (count == 0) continue;
                byte value = one[0];
                if (value == HidProtocol.AckStart)
                {
                    byte[] ack = new byte[2];
                    await ReadExactlyAsync(ack, stop.Token).ConfigureAwait(false);
                    TaskCompletionSource<bool>? completion;
                    lock (pendingLock) pending.TryGetValue(ack[0], out completion);
                    completion?.TrySetResult(ack[1] == 0);
                }
                else throw new InvalidDataException($"Unexpected CDC response byte 0x{value:X2}; expected ACK 0x{HidProtocol.AckStart:X2}.");
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception) when (stop.IsCancellationRequested) { }
        catch (Exception ex) { MarkConnectionLost(ex); }
    }

    private void MarkConnectionLost(Exception ex)
    {
        if (Interlocked.CompareExchange(ref connectionFailure, ex, null) is not null)
            return;
        lock (pendingLock)
        {
            foreach (TaskCompletionSource<bool> completion in pending.Values)
                completion.TrySetException(new IOException("CDC serial connection was lost.", ex));
        }
        try { ConnectionLost?.Invoke(ex); } catch { }
    }

    private async Task ReadExactlyAsync(Memory<byte> buffer, CancellationToken token)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int count = await port.BaseStream.ReadAsync(buffer[offset..], token).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException("CDC serial stream closed while reading ACK.");
            offset += count;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        stop.Cancel();
        port.Close();
        try { await readTask.ConfigureAwait(false); } catch { }
        port.Dispose();
        stop.Dispose();
        // A SendReportAsync interrupted by stop can still be unwinding and
        // releasing this semaphore after DisposeAsync returns.
    }
}
