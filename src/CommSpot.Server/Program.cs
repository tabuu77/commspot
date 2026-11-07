using CommSpot.Server;

var app = CommSpotServer.Build(args);
await app.RunAsync().ConfigureAwait(false);
