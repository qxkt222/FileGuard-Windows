using FileGuard.SelfCheck;

var verbose = args.Contains("--verbose");
var registry = new CheckRegistry();
Checks.Register(registry);
return Runner.Run(registry, verbose);
