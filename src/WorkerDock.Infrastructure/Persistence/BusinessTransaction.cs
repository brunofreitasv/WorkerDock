using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using WorkerDock.Application.Common;

namespace WorkerDock.Infrastructure.Persistence;

public sealed class BusinessTransaction(TenantDatabaseOptions options)
{
    public async ValueTask<T> RunAsync<T>(Func<DbConnection, DbTransaction, CancellationToken, ValueTask<T>> action, CancellationToken ct)
    {
        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await using var lockCommand = connection.CreateCommand();
        lockCommand.Transaction = transaction;
        lockCommand.CommandText = "DECLARE @result int; EXEC @result = sys.sp_getapplock @Resource=N'WorkerDock.Tenant.Business', @LockMode=N'Exclusive', @LockOwner=N'Transaction', @LockTimeout=10000; SELECT @result;";
        var result = Convert.ToInt32(await lockCommand.ExecuteScalarAsync(ct));
        if (result < 0) throw new WorkerDockException("BusinessLockUnavailable");
        var value = await action(connection, transaction, ct);
        await transaction.CommitAsync(ct);
        return value;
    }
}
