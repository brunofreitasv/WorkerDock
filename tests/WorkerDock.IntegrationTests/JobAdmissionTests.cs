using WorkerDock.Infrastructure.Dispatch;
using WorkerDock.Infrastructure.Executions;
using WorkerDock.Application.Common;
using WorkerDock.Contracts.Templates;
using System.Text.Json;
namespace WorkerDock.IntegrationTests;

public sealed class JobAdmissionTests
{
    private static AdmissionCandidate Work(string mode = "Allow", bool held = false, int backlog = 10) => new(Guid.NewGuid(), mode, backlog, held);
    [Fact]
    public async Task Actual_retry_review_and_reconciliation_keep_later_shared_work_behind_exclusive_execution()
    {
        var clock=new AttemptLifecycleTests.Clock(); var descriptor=AttemptLifecycleTests.Descriptor();
        var first=new InMemoryAttemptStore(clock,descriptor,AttemptLifecycleTests.Policy());
        var next=Work("Queue"); var shared=Work();
        AdmissionDecision Decide(int limit) => JobAdmission.Decide(first.Snapshot.PermitRetained
            ? [new(descriptor.ExecutionId,"Queue",10,true),next,shared] : [next,shared],limit);
        var lease=(await first.BeginAsync(first.Intent,"worker",default)).Lease!;
        await first.CompleteAsync(lease,new(new TemplateResult("TransientFailure","Busy",false,JsonSerializer.SerializeToElement(new {}),null),"Completed",true,false),default);
        Assert.Equal("RetryWaiting",first.Snapshot.State); Assert.Empty(Decide(3).Admit);
        var retry=Assert.Single(first.Retries); clock.Now=retry.DueUtc;
        var retryLease=(await first.BeginAsync(retry,"worker",default)).Lease!;
        Assert.Empty(Decide(0).Admit);
        await first.CompleteAsync(retryLease,new(null,"ProcessFailed",false,true),default);
        Assert.Equal("NeedsReview",first.Snapshot.State); Assert.Empty(Decide(3).Admit);
        await first.ResolveAsync(descriptor.ExecutionId,"Cancel","reconciled target effects",new ActorContext("manager","Anonymous",Guid.NewGuid()),default);
        Assert.Empty(Decide(0).Admit); Assert.Equal(new[] { next.ExecutionId },Decide(1).Admit);
        Assert.DoesNotContain(shared.ExecutionId,Decide(1).Admit);
    }
    [Fact]
    public void Retry_and_review_hold_exclusive_permit()
    {
        var retryOrReview = Work("Queue", true); var later = Work();
        Assert.Empty(JobAdmission.Decide([retryOrReview, later], 3).Admit);
        Assert.Empty(JobAdmission.Decide([retryOrReview, later], 0).Admit);
        Assert.Equal(new[] { later.ExecutionId }, JobAdmission.Decide([later], 1).Admit);
    }
    [Fact]
    public void Exclusive_waiter_prevents_later_shared_work_overtaking()
    {
        var holder = Work(held: true); var exclusive = Work("Queue"); var later = Work();
        Assert.Empty(JobAdmission.Decide([holder, exclusive, later], 3).Admit);
        Assert.Equal(new[] { exclusive.ExecutionId }, JobAdmission.Decide([exclusive, later], 3).Admit);
        Assert.Equal(new[] { later.ExecutionId }, JobAdmission.Decide([later], 3).Admit);
    }
    [Fact]
    public void Job_decrease_blocks_new_admission_but_preserves_existing_holders()
    {
        var first = Work(held: true); var second = Work(held: true); var third = Work();
        Assert.Empty(JobAdmission.Decide([first, second, third], 1).Admit);
        Assert.Empty(JobAdmission.Decide([third], 0).Admit);
        Assert.Equal(new[] { third.ExecutionId }, JobAdmission.Decide([third], 1).Admit);
    }
    [Fact]
    public void Skip_and_backlog_overflow_are_recorded_in_order()
    {
        var holder = Work("Queue", true); var skip = Work("Skip"); var waiting = Work("Queue", backlog: 1); var overflow = Work("Queue", backlog: 1);
        var decision = JobAdmission.Decide([holder, skip, waiting, overflow], 1);
        Assert.Empty(decision.Admit); Assert.Equal(new[] { skip.ExecutionId, overflow.ExecutionId }, decision.Skip);
        Assert.Equal("SkippedOverlap",decision.SkipReasons[skip.ExecutionId]); Assert.Equal("SkippedBacklog",decision.SkipReasons[overflow.ExecutionId]);
    }
    [Fact]
    public void Shared_admissions_obey_limit_across_an_ordered_batch()
    {
        var work = Enumerable.Range(0, 8).Select(_ => Work()).ToArray();
        Assert.Equal(work.Take(3).Select(x => x.ExecutionId), JobAdmission.Decide(work, 3).Admit);
    }
}
