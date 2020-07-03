# InfuseSync

InfuseSync is a plugin for Emby and Jellyfin media servers that tracks all media changes to decrease sync times with [Infuse](https://firecore.com/infuse) clients.

## Build & Installation

1. Install [.NET Core SDK](https://dotnet.microsoft.com/download)

2. Build the plugin with following command:

```
dotnet publish --configuration Release
```

3. Place the resulting .dll file that can be found in ```InfuseSync/bin/emby``` or ```InfuseSync/bin/jellyfin``` directory into a plugin folder for the corresponding media server.
