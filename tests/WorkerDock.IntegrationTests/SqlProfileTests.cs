using WorkerDock.Application.Common;
using WorkerDock.Infrastructure.Connections;
namespace WorkerDock.IntegrationTests;
public sealed partial class SqlProfileTests
{
    private static ActorContext Actor => new("manager","Anonymous",Guid.NewGuid());
    [Fact]
    public async Task Revisions_survive_rotation_retirement_and_caller_mutation()
    {
        var store=new InMemoryConnectionProfiles(); var original=SqlTemplateTests.Profile();
        await store.SaveAsync(original,null,Actor,default); var current=await store.GetCurrentAsync(original.ProfileId,default);
        await store.SaveAsync(original with { Server="new-server" },current.Version,Actor,default);
        var rotated=await store.GetCurrentAsync(original.ProfileId,default);
        Assert.NotEqual(current.Current.ProfileRevisionId,rotated.Current.ProfileRevisionId);
        Assert.Equal(original.Server,(await store.GetRevisionAsync(current.Current.ProfileRevisionId,default)).Server);
        await Assert.ThrowsAsync<WorkerDockException>(async()=>await store.RetireAsync(original.ProfileId,current.Version,Actor,default));
        await store.RetireAsync(original.ProfileId,rotated.Version,Actor,default);
        Assert.Empty(await store.ListAsync(default));
        Assert.Equal(original.Server,(await store.GetRevisionAsync(current.Current.ProfileRevisionId,default)).Server);
        var error=await Assert.ThrowsAsync<WorkerDockException>(async()=>await store.GetCurrentAsync(original.ProfileId,default)); Assert.Equal("ProfileRetired",error.Code);
    }
    [Fact]
    public async Task Profile_validation_rejects_password_modes_and_duplicate_names()
    {
        var store=new InMemoryConnectionProfiles(); var profile=SqlTemplateTests.Profile();
        await Assert.ThrowsAsync<WorkerDockException>(async()=>await store.SaveAsync(profile with { Authentication="RawConnectionString" },null,Actor,default));
        await store.SaveAsync(profile,null,Actor,default);
        await Assert.ThrowsAsync<WorkerDockException>(async()=>await store.SaveAsync(SqlTemplateTests.Profile(),null,Actor,default));
        Assert.Single(await store.ListAsync(default));
    }
    [Fact]
    public async Task Profile_snapshot_leaves_room_for_bounded_child_settings()
    {
        var store=new InMemoryConnectionProfiles();var profile=SqlTemplateTests.Profile() with { AllowedCatalogs=Enumerable.Range(0,64).Select(i=>i+new string('x',120)).ToArray() };
        var error=await Assert.ThrowsAsync<WorkerDockException>(async()=>await store.SaveAsync(profile,null,Actor,default));
        Assert.Equal("ValidationFailed",error.Code);Assert.Empty(await store.ListAsync(default));
    }}
