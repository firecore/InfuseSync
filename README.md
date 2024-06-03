# InfuseSync

InfuseSync is a plugin for Emby and Jellyfin media servers that tracks all media changes to decrease sync times with [Infuse](https://firecore.com/infuse) clients.

## Standard Installation (recommended)
View the InfuseSync [install guide](https://support.firecore.com/hc/articles/23885208585367) for Emby and Jellyfin.

## Build and Install Manually

1. Install [.NET Core SDK](https://dotnet.microsoft.com/download)

2. Build the plugin with following command:

```
dotnet publish --configuration Release
```

3. Place the resulting .dll file that can be found in ```InfuseSync/bin/emby``` or ```InfuseSync/bin/jellyfin``` directory into a plugin folder for the corresponding media server.
