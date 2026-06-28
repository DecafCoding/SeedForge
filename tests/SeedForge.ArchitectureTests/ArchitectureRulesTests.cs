using NetArchTest.Rules;
using SeedForge.Data;

namespace SeedForge.ArchitectureTests
{
    /// <summary>Build-enforced dependency boundaries for the vertical-slice architecture.</summary>
    public class ArchitectureRulesTests
    {
        private static readonly System.Reflection.Assembly SeedForgeAssembly =
            typeof(ApplicationDbContext).Assembly;

        [Fact]
        public void Services_should_not_depend_on_Features()
        {
            var result = Types.InAssembly(SeedForgeAssembly)
                .That().ResideInNamespace("SeedForge.Services")
                .ShouldNot().HaveDependencyOn("SeedForge.Features")
                .GetResult();

            Assert.True(result.IsSuccessful, FormatFailures(result));
        }

        [Fact]
        public void Domain_should_not_depend_on_infrastructure()
        {
            var result = Types.InAssembly(SeedForgeAssembly)
                .That().ResideInNamespace("SeedForge.Domain")
                .ShouldNot().HaveDependencyOnAny(
                    "SeedForge.Services",
                    "SeedForge.Features",
                    "SeedForge.Data",
                    "Microsoft.EntityFrameworkCore")
                .GetResult();

            Assert.True(result.IsSuccessful, FormatFailures(result));
        }

        private static string FormatFailures(TestResult result) =>
            result.FailingTypeNames is { } names
                ? "Offending types: " + string.Join(", ", names)
                : "Architecture rule violated.";
    }
}
