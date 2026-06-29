using SeedForge.Services.YouTube;

namespace SeedForge.UnitTests
{
    /// <summary>ChannelRef.Parse classifies channel ids, /channel/ URLs, @handles, and legacy custom references.</summary>
    public class ChannelRefTests
    {
        [Theory]
        [InlineData("UC_x5XG1OV2P6uZZ5FSM9Ttw")]
        [InlineData("https://www.youtube.com/channel/UC_x5XG1OV2P6uZZ5FSM9Ttw")]
        public void Parse_recognizes_channel_ids(string input)
        {
            var reference = ChannelRef.Parse(input);
            Assert.Equal(ChannelRefKind.Id, reference.Kind);
            Assert.Equal("UC_x5XG1OV2P6uZZ5FSM9Ttw", reference.Value);
        }

        [Theory]
        [InlineData("@GoogleDevelopers")]
        [InlineData("https://www.youtube.com/@GoogleDevelopers")]
        public void Parse_recognizes_handles(string input)
        {
            var reference = ChannelRef.Parse(input);
            Assert.Equal(ChannelRefKind.Handle, reference.Kind);
            Assert.Equal("@GoogleDevelopers", reference.Value);
        }

        [Theory]
        [InlineData("https://www.youtube.com/c/LinusTechTips", "LinusTechTips")]
        [InlineData("https://www.youtube.com/user/LinusTechTips", "LinusTechTips")]
        [InlineData("LinusTechTips", "LinusTechTips")]
        public void Parse_falls_back_to_custom(string input, string expected)
        {
            var reference = ChannelRef.Parse(input);
            Assert.Equal(ChannelRefKind.Custom, reference.Kind);
            Assert.Equal(expected, reference.Value);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void Parse_throws_on_empty(string input)
        {
            Assert.Throws<ArgumentException>(() => ChannelRef.Parse(input));
        }
    }
}
