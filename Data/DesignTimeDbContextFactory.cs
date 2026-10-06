using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DashTudo.Web.Data;

/// <summary>Usado só pelo "dotnet ef" para gerar migrations sem precisar subir a aplicação.</summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            // Versão fixa para não precisar conectar no banco ao gerar migrations.
            .UseMySql("Server=localhost;Database=dashtudo;User=dashtudo;Password=design-time;",
                new MariaDbServerVersion(new Version(10, 11)))
            .Options;
        return new AppDbContext(options);
    }
}
