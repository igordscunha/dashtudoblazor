using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Migrations;

namespace DashTudo.Web.Data;

/// <summary>Usado só pelo "dotnet ef" para gerar migrations sem precisar subir a aplicação.</summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseMySQL("Server=localhost;Database=dashtudo;User=dashtudo;Password=design-time;")
            .ReplaceService<IHistoryRepository, MariaDbCompatibleHistoryRepository>()
            .Options;
        return new AppDbContext(options);
    }
}
