// The fixtures bind sockets and share process-wide environment variables, so test classes run one at a time.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
