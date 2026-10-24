using SelfService.Foundation.Host;

var options = FoundationHostConfiguration.Load();
await FoundationApplication.Build(options, args).RunAsync();

public partial class Program;
