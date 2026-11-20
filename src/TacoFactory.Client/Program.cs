using TacoFactory.Client;

var options = ClientOptions.Parse(args);
using var game = new GameClient(options);
game.Run();
