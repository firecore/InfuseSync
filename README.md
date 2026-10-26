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

## Contributing

Bug reports, proposals, and pull requests are welcome. Keep in mind, that any direct communication, including the issues, comments, pull request, etc., must be human-written. In short, if you are posting anything, the communication part of the output must be your own words without the use of any automated tools.

When reporting bugs, please provide detailed reproduction steps. Also, specify the exact versions of Emby/Jellyfin, InfuseSync plugin, and Infuse client. Describe the devices running both the client and the server, including their operating systems and version numbers. If possible, attach the Emby/Jellyfin server logs and the diagnostic code from the "Report a Problem" menu in Infuse.

Try to keep pull requests small and focused on a single, specific topic. This makes them easier to review, discuss, approve, and merge. The large the changes, the less likely they are to be merged. Furthermore, we expect you to understand all the submitted code, be able to describe its purpose, handle review feedback and implement the suggested changes.

If your commit fixes an issue, include it in the _description_ of the PR (not in the title, or the commit message) using [GitHub keywords](https://docs.github.com/en/issues/tracking-your-work-with-issues/linking-a-pull-request-to-an-issue)
such as "Fixes #123".