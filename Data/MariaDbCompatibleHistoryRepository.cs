using System.Text;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;

namespace DashTudo.Web.Data;

/// <summary>
/// Tabela de histórico de migrations compatível com MySQL <b>e</b> MariaDB.
/// <para>
/// O provedor MySql.EntityFrameworkCore trava as migrations com <c>GET_LOCK(nome, -1)</c>. No MySQL, -1 significa
/// "esperar indefinidamente"; no MariaDB um timeout negativo retorna NULL e o provedor quebra com
/// <c>InvalidCastException: DBNull → Int64</c>. Como a classe original é internal, esta reimplementa o
/// repositório usando um timeout positivo, aceito pelos dois bancos.
/// </para>
/// </summary>
public class MariaDbCompatibleHistoryRepository(HistoryRepositoryDependencies dependencies) : HistoryRepository(dependencies)
{
    private const string LockName = "__EFMigrationsLock";
    private const int LockTimeoutSeconds = 600;

    public override LockReleaseBehavior LockReleaseBehavior => LockReleaseBehavior.Connection;

    protected override string ExistsSql =>
        "SELECT COUNT(*) FROM information_schema.tables " +
        $"WHERE table_schema = DATABASE() AND table_name = {Literal(TableName)};";

    protected override bool InterpretExistsResult(object? value) =>
        value is not null and not DBNull && Convert.ToInt64(value) > 0;

    public override string GetCreateIfNotExistsScript()
    {
        var script = GetCreateScript();
        const string create = "CREATE TABLE ";
        var i = script.IndexOf(create, StringComparison.OrdinalIgnoreCase);
        return i < 0 ? script : script.Insert(i + create.Length, "IF NOT EXISTS ");
    }

    // Blocos IF só existem dentro de procedures no MySQL/MariaDB; usados apenas por "dotnet ef migrations script --idempotent".
    public override string GetBeginIfNotExistsScript(string migrationId) =>
        throw new NotSupportedException("Scripts idempotentes não são suportados no MySQL/MariaDB.");

    public override string GetBeginIfExistsScript(string migrationId) =>
        throw new NotSupportedException("Scripts idempotentes não são suportados no MySQL/MariaDB.");

    public override string GetEndIfScript() =>
        throw new NotSupportedException("Scripts idempotentes não são suportados no MySQL/MariaDB.");

    public override IMigrationsDatabaseLock AcquireDatabaseLock()
    {
        Dependencies.MigrationsLogger.AcquiringMigrationLock();
        EnsureAcquired(Sql($"SELECT GET_LOCK('{LockName}', {LockTimeoutSeconds});").ExecuteScalar(Parameters()));
        return new ReleasableLock(this);
    }

    public override async Task<IMigrationsDatabaseLock> AcquireDatabaseLockAsync(CancellationToken cancellationToken = default)
    {
        Dependencies.MigrationsLogger.AcquiringMigrationLock();
        EnsureAcquired(await Sql($"SELECT GET_LOCK('{LockName}', {LockTimeoutSeconds});").ExecuteScalarAsync(Parameters(), cancellationToken));
        return new ReleasableLock(this);
    }

    private static void EnsureAcquired(object? result)
    {
        if (result is null or DBNull || Convert.ToInt64(result) != 1)
            throw new TimeoutException($"Não foi possível obter o lock de migrations em {LockTimeoutSeconds}s. Outra instância está aplicando migrations?");
    }

    private string Literal(string value) =>
        Dependencies.TypeMappingSource.GetMapping(typeof(string)).GenerateSqlLiteral(value);

    private IRelationalCommand Sql(string sql) => Dependencies.RawSqlCommandBuilder.Build(sql);

    private RelationalCommandParameterObject Parameters() => new(
        Dependencies.Connection, null, null, Dependencies.CurrentContext.Context, Dependencies.CommandLogger, CommandSource.Migrations);

    private sealed class ReleasableLock(MariaDbCompatibleHistoryRepository repository) : IMigrationsDatabaseLock
    {
        private const string ReleaseSql = $"SELECT RELEASE_LOCK('{LockName}');";

        public IHistoryRepository HistoryRepository => repository;

        public void Dispose() => repository.Sql(ReleaseSql).ExecuteScalar(repository.Parameters());

        public async ValueTask DisposeAsync() => await repository.Sql(ReleaseSql).ExecuteScalarAsync(repository.Parameters());
    }
}
