// Program.cs
using CardManagement.Data;
using CardManagement.Models;
using CardManagement.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// 1. Register the DbContextFactory first. 
// By default, this registers IDbContextFactory AND DbContextOptions as Singletons.
// This allows the factory to be used safely in background threads (Task.Run).
builder.Services.AddDbContextFactory<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")));

// 2. Register the Scoped AppDbContext for use in Controllers and Scoped Services.
// We use the factory to create the context, ensuring it uses the same configuration.
builder.Services.AddScoped(sp =>
    sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext());

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddHttpClient();
builder.Services.AddScoped<TrackingApiService>();
builder.Services.AddSingleton<HexProtocolService>();
builder.Services.AddScoped<DeviceSyncService>();
builder.Services.AddScoped<PlatformSyncService>();
builder.Services.AddScoped<CardListPollingService>();

var app = builder.Build();

// Ensure DB exists and seed default admin user
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();

    if (!db.Users.Any())
    {
        db.Users.Add(new User
        {
            Username = "admin",
            PasswordHash = "admin",
            Role = "Admin",
            CompanyId = null
        });
        db.SaveChanges();
    }
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAuthorization();
app.MapControllers();
app.MapFallbackToFile("index.html");

app.Run();