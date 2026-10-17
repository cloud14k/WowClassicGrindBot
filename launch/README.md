# Twinkle14k launcher and updater

`Launch.exe` uses WinSCP to synchronize the program directory from one fixed FTP directory. There are no version folders, `latest.json`, or manifest files. WinSCP downloads files it considers changed; the launcher stages the result, applies file changes with rollback support, then starts `espidf.exe`. If synchronization fails, the launcher displays the error and exits without starting the installed program. WinSCP logs are kept in `<install root>/logs/launcher/` with a timestamp, and the error message includes the end of the log.

## FTP directory

Publish the complete contents of `BaoServer`'s `dotnet publish` output directly to one remote directory:

```text
FTP root/
  Twinkle14k/
    app/
      espidf.exe
      *.dll
      ...
```

Set `RemoteApplicationDirectory` in `updater.json` to the FTP path for that `app` directory, currently `/FTP/DATAS/share/Twinkle14k/app`. On each release, upload the new publish output into the same directory, allowing files to be replaced. Remove obsolete program files from the FTP directory so the next synchronization removes them from the local program folder too.

## Client installation

The client installation layout is:

```text
Twinkle14k/
  launch/     Launch.exe, updater.json, updater.example.json
  app/        installed program files
  json/       user/runtime data, kept outside program updates
```

Install WinSCP on each client computer and save a site named `Twinkle14kUpdates`. Use a secure protocol supported by the server, preferably SFTP or FTPS. Then copy the complete installation folder and configure `launch/updater.json`:

```json
{
  "InstallRoot": "",
  "WinScpPath": "",
  "WinScpSession": "Twinkle14kUpdates",
  "RemoteApplicationDirectory": "/FTP/DATAS/share/Twinkle14k/app",
  "ApplicationRelativePath": "app/espidf.exe",
  "ApplicationArguments": [],
  "BrowserUrl": "http://localhost:5000",
  "PreservedPaths": [
    "appsettings.json",
    "addon_config.json",
    "frame_config.json",
    "data_config.json",
    "decision-settings.json",
    "input-backend.json",
    "training-data/",
    "logs/"
  ]
}
```

`WinScpPath` can stay empty when WinSCP is installed in its standard location. For a portable copy, put `WinSCP.exe` and `WinSCP.ini` together, then set the full path of `WinSCP.exe` in this field. A relative path such as `WinSCP.exe` is resolved from the `launch` directory. The launcher starts WinSCP from that directory so it loads the saved site from the neighboring INI file. The saved site credentials are in that INI file. Do not use plain FTP over an untrusted network.

## Build and publish

From the repository root, publish the program and launcher:

```powershell
dotnet publish .\BaoServer\BlazorServer.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o .\artifacts\Twinkle14k\app
dotnet publish .\launch\Launch.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o .\artifacts\Twinkle14k\launch
Copy-Item .\launch\updater.example.json .\artifacts\Twinkle14k\launch\updater.json
```

Upload the contents of `artifacts\Twinkle14k\app` into the FTP `Twinkle14k/app` directory. Keep `json/` separate from this directory. Run `launch/Launch.exe` on the client to synchronize and start the program. Pass `--no-start` to synchronize without starting it, or `--install-root <path>` if the installation root is not the parent of `launch/`.

## Incrementally upload a release

From the repository root, preview the differences between the local publish folder and the FTP program folder:

```powershell
.\launch\Upload-App.ps1
```

The preview does not upload anything. When the list is correct, run:

```powershell
.\launch\Upload-App.ps1 -Apply
```

After the synchronization, the separate WinSCP console stays open in interactive mode so you can inspect its output; type `exit` there when you are done. Add `-CloseWinScpWhenDone` to close WinSCP automatically. The PowerShell script itself does not wait for Enter after WinSCP closes.

The uploader reads `WinScpSession`, `RemoteApplicationDirectory`, and `WinScpPath` from `launch/updater.json` (or the published launcher config/example). It defaults to `artifacts\Twinkle14k\app`, uploads changed files only, and skips PDB files, logs, training data, and local user configuration files. Use `-IncludeConfigFiles` only when you intentionally want to publish the config files. Use `-DeleteObsoleteFiles` with `-Apply` only when you also want to remove remote program files that no longer exist locally; the excluded data/config files are not deleted.

The updater preserves existing app configuration files, `input-backend.json`, `training-data/`, `logs/`, and `.log` files. `input-backend.json` is always preserved, including for clients whose older `updater.json` does not list it. The sibling `json/` directory is outside the update scope. FTP synchronization compares file timestamps and sizes; keep upload timestamps accurate so same-size changes are detected reliably.
