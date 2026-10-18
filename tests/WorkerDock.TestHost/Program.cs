using Quartz;
using Microsoft.Data.SqlClient;
using WorkerDock.Quartz.Hosting;
using WorkerDock.TestHost;

try
{
    var instanceId = Guid.Parse(Environment.GetEnvironmentVariable("WORKERDOCK_QUARTZ_INSTANCE")!);
    var boot = Environment.GetEnvironmentVariable("WORKERDOCK_QUARTZ_BOOT")!;
    var role = Environment.GetEnvironmentVariable("WORKERDOCK_QUARTZ_ROLE");
    if (role is not ("publisher" or "execution")) throw new InvalidOperationException("Invalid test role");
    await using var topology = new QuartzTopology(new(Environment.GetEnvironmentVariable("WORKERDOCK_QUARTZ_CONNECTION")!, instanceId));
    var options = new QuartzNodeOptions(instanceId, boot, 4) { TemplateLimits = new Dictionary<string, int> { ["template-probe"] = 1 } };
    var scheduler = role == "publisher" ? await topology.CreateExecutionPublisherAsync(options, default) : await topology.CreateExecutionAsync(options, default);
    Console.WriteLine("READY " + scheduler.Status);
    while (await Console.In.ReadLineAsync() is { } line)
    {
        var parts = line.Split(' ');
        if (parts[0] == "STOP") break;
        if (parts[0] == "ARM" && role == "execution")
        {
            await scheduler.Start();
            await WaitForRegistrationAsync(Environment.GetEnvironmentVariable("WORKERDOCK_QUARTZ_CONNECTION")!, boot);
            Console.WriteLine("ARMED " + scheduler.Status);
        }
        if (parts[0] == "STANDBY" && role == "execution")
        {
            await scheduler.Standby();
            Console.WriteLine("STANDBY " + scheduler.Status);
        }
        if (parts[0] == "PING") Console.WriteLine("PONG");
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

static async Task WaitForRegistrationAsync(string connectionString, string boot)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    await using var connection = new SqlConnection(connectionString);
    await connection.OpenAsync(timeout.Token);
    while (true)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM [workerdock].WDE_QRTZ_SCHEDULER_STATE WHERE SCHED_NAME = N'WorkerDock.Execute' AND INSTANCE_NAME = @Boot";
        command.Parameters.Add(new SqlParameter("@Boot", System.Data.SqlDbType.NVarChar, 200) { Value = boot });
        if (Convert.ToInt32(await command.ExecuteScalarAsync(timeout.Token)) == 1) return;
        await Task.Delay(100, timeout.Token);
    }
}
