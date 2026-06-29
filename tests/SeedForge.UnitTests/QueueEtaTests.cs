using SeedForge.Services.Queues;

namespace SeedForge.UnitTests
{
    /// <summary>Drain-time arithmetic for the queue views (<see cref="QueueEta"/>).</summary>
    public sealed class QueueEtaTests
    {
        [Fact]
        public void Eta_multiplies_pending_by_interval()
        {
            // 3 pending × 1800s = 5400s = 90 minutes.
            Assert.Equal(TimeSpan.FromMinutes(90), QueueEta.Eta(3, 1800));
        }

        [Fact]
        public void Eta_is_zero_for_empty_queue()
        {
            Assert.Equal(TimeSpan.Zero, QueueEta.Eta(0, 1800));
        }

        [Fact]
        public void Eta_is_zero_for_nonpositive_interval()
        {
            Assert.Equal(TimeSpan.Zero, QueueEta.Eta(5, 0));
        }

        [Fact]
        public void Format_empty_queue_reads_as_dash()
        {
            Assert.Equal("—", QueueEta.Format(0, 1800));
        }

        [Fact]
        public void Format_renders_hours_and_minutes()
        {
            Assert.Equal("1h 30m", QueueEta.Format(3, 1800));
        }
    }
}
