using BlazorChat;
using BlazorChat.Components;
using Hibira.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
// Reads the "Hibira" section: AppId, Key, Secret (HIBIRA__APPID, HIBIRA__KEY, HIBIRA__SECRET).
builder.Services.AddHibira();

var app = builder.Build();

app.UseStaticFiles();
app.UseAntiforgery();

app.MapHibiraHub<ChatHub>("/hubs/chat");
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();
