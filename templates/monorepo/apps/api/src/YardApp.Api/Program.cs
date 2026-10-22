using Yard.AspNetCore;
using YardApp.Api.Hosting;

var builder = WebApplication.CreateBuilder(args.FirstOrDefault() == "yard" ? [] : args);
builder.AddApplication();
var app = builder.Build();
app.UseYard();
if (await app.RunYardCommandAsync(args))
    return;
app.Run();

public partial class Program;
