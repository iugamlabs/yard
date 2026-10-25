using YardApp.Api.Hosting;

var builder = WebApplication.CreateBuilder(args);
builder.AddApplication();
var app = builder.Build();
app.UseApplication();
app.Run();

public partial class Program;
