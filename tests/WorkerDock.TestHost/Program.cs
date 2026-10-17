using Quartz;
using WorkerDock.Quartz.Hosting;
using WorkerDock.TestHost;

try
{
    var instanceId = Guid.Parse(Environment.GetEnvironmentVariable("WORKERDOCK_QUARTZ_INSTANCE")!);
    var boot = Environment.GetEnvironmentVariable("WORKERDOCK_QUARTZ_BOOT")!;
    var role = Environment.GetEnvironmentVariable("WORKERDOCK_QUARTZ_ROLE");
    await using var topology = new QuartzTopology(new(Environment.GetEnvironmentVariable("WORKERDOCK_QUARTZ_CONNECTION")!, instanceId));
    var options = new QuartzNodeOptions(instanceId, boot, 4) { TemplateLimits = new Dictionary<string, int> { ["template-probe"] = 1 } };
    var scheduler = role == "publisher" ? await topology.CreateExecutionPublisherAsync(options, default) : await topology.CreateExecutionAsync(options, default);
    Console.WriteLine("READY " + scheduler.Status);
    if (role != "publisher") await scheduler.Start();
    while (await Console.In.ReadLineAsync() is { } line)
    {
        var parts = line.Split(' ');
        if (parts[0] == "STOP") break;
        if (parts[0] == "RELEASE") ProbeJob.Release(parts[1]);
        if (parts[0] == "PUBLISH")
        {
            var id = Guid.ParseExact(parts[1], "N").ToString("N");
            await scheduler.ScheduleJob(JobBuilder.Create<ProbeJob>().WithIdentity(id)
                .UsingJobData("ExecutionId", Guid.NewGuid().ToString("N"))
                .UsingJobData("DeliveryId", id).UsingJobData("AttemptOrdinal", "1").Build(),
                TriggerBuilder.Create().WithIdentity(id).WithExecutionGroup("template-probe").StartNow().Build());
            Console.WriteLine("PUBLISHED " + id);
        }
    }
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await scheduler.Shutdown(true, timeout.Token);
    return 0;
}
catch
{
    Console.Error.WriteLine("QUARTZ_TEST_HOST_FAILED");
    return 1;
}
