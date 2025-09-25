

var builder = WebApplication.CreateBuilder(args);
// Add services to the container.
builder.Services.AddRazorPages();
builder.Services.AddSession();
// Bind AzureOpenAI config

builder.Services.Configure<AspNetWebApp.Options.AzureOpenAIOptions>(
    builder.Configuration.GetSection("AzureOpenAI"));
builder.Services.Configure<AspNetWebApp.Options.AzureSearchOptions>(
    builder.Configuration.GetSection("AzureSearch"));
builder.Services.Configure<AspNetWebApp.Options.AzureBlobOptions>(
    builder.Configuration.GetSection("AzureBlob"));


var app = builder.Build();

// Access the config (example usage)
var azureOptions = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<AspNetWebApp.Options.AzureOpenAIOptions>>().Value;
Console.WriteLine($"AzureOpenAI Endpoint: {azureOptions.Endpoint}");

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}


app.UseHttpsRedirection();
app.UseSession();

app.UseRouting();

app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();

app.Run();
