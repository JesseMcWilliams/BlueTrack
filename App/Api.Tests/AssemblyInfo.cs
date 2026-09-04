using Xunit;

// Every test in this assembly (Integration and Contract alike) runs against
// one real, shared BlueTrackTest database -- singleton config rows
// (web.app_config), a shared lock table, and the same handful of synthetic
// accounts (TestAccount01-04) are all mutated across multiple test classes
// (AccountProgressEditingTests, RiskExceptionsWorkflowTests,
// AdminControllersFunctionalTests, etc.). xUnit runs test classes in
// separate collections in parallel by default, which raced two of those
// classes against TestAccount03's lock state and produced an intermittent
// Conflict instead of the expected BadRequest (confirmed directly -- the
// same test passed reliably in isolation but failed once run alongside
// other classes). Serializing execution trades a small amount of wall-clock
// time (this suite runs in under a second either way) for eliminating that
// whole class of flakiness.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
