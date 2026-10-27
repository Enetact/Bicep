// Bound independent collections competing for native filesystem, process and
// local-host resources. Keep default ordering, explicit concurrency within
// tests and the separately isolated capacity collection's existing rules.
// This is test-resource backpressure, not a production limit or deadline waiver;
// acceptance still requires the actual unfiltered Foundation profile.
[assembly: Xunit.CollectionBehavior(MaxParallelThreads = 4)]
