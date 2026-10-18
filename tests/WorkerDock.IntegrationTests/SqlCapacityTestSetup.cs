using WorkerDock.Application.Workers;
using WorkerDock.Infrastructure.Persistence;
using WorkerDock.Infrastructure.Workers;
namespace WorkerDock.IntegrationTests;
internal static class SqlCapacityTestSetup
{
    public static async Task ReadyAsync(TenantDatabaseOptions options,params string[] boots)
    {
        var store=new SqlCapacityStore(options);
        await store.TransactAsync((state,now) =>
        {
            foreach(var template in state.Limits.Keys.ToArray()) state.SetTemplateLimit(template,3);
            foreach(var boot in boots.Length==0 ? new[] { "sql-fixture" } : boots)
            {
                var id=Guid.NewGuid(); state.Register(id,boot,state.CatalogDigest,3,now);
                state.Acknowledge(id,boot,state.DesiredRevision,now);
            }
            Assert.True(state.Promote(now)); return true;
        },default);
    }
}
