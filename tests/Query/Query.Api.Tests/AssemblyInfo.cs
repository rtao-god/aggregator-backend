using Xunit;

// Query API factories bootstrap the minimal host through one process-wide connection-string setting.
// Serializing this assembly prevents one factory from restoring that setting while another host starts.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
