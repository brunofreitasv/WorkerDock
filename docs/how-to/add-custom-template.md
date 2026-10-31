# Add a custom template

A template is reviewed .NET code installed with WorkerDock. A dashboard user can choose an installed template, but cannot upload code. Use this guide when the built-in SQL Server and HTTP templates do not fit your job.

## Before you start

- Read the [template authoring reference](../reference/template-authoring.md) for exact interfaces, schema limits, and safe logging rules.
- Look at [the sample template](../../examples/WorkerDock.SampleTemplate/SampleTemplate.cs) for a small implementation.
- Plan to deploy the Web, Worker, and Executor from one compatible release.

## Add it in four steps

1. Create a .NET package that references `WorkerDock.Contracts` and implements `IWorkerTemplate`. Give the template a stable ID and version. Create its descriptor with `TemplateDescriptorMetadata.WithDigest`.
2. Register the descriptor explicitly in `src/WorkerDock.Web/Program.cs` and `src/WorkerDock.Worker/Program.cs`. Register the handler in `src/WorkerDock.Executor/Program.cs`. A job saved in the dashboard does not load a package.
3. Build and review the package. Deploy matching Web, Worker, and Executor binaries together. The Worker registers the trusted descriptor in the tenant SQL catalog.
4. Start the runtime. Check catalog registration and readiness. In **Jobs**, choose the template, save a disabled draft, review it, and enable it.

Changing a descriptor's metadata requires a **new version**. Existing jobs keep their pinned version. Keep credentials as logical secret references; return only safe results and logs. See [tenant configuration](configure-tenants.md) for runtime settings and [execution guarantees](../reference/execution-guarantees.md) for retries and uncertain external effects.

## Check your work

Run the relevant [developer checks](../validation/development-checks.md). The [authoring reference](../reference/template-authoring.md#verification) includes focused Windows process checks. Exercise failure, cancellation, and retry behavior before deploying.
