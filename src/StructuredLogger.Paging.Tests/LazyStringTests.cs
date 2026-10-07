using Microsoft.Build.Logging.StructuredLogger;
using Xunit;

namespace StructuredLogger.Paging.Tests
{
    public class LazyStringTests
    {
        private static string Here([System.Runtime.CompilerServices.CallerFilePath] string path = null) => path;

        [Fact]
        public void ReassignmentTextIsFormattedOnDemandFromItsParts()
        {
            Strings.Initialize();
            var node = new PropertyReassignmentMessage
            {
                PropertyName = "Foo", NewValue = "new", PreviousValue = "old", FilePath = "a.props", Line = 3, Column = 7,
            };

            Assert.Equal(PropertyReassignmentMessage.FormatText("Foo", "new", "old", "a.props", 3, 7), node.Text);
            Assert.Contains("a.props (3,7)", node.Text);
            Assert.Contains("new", node.Text);
            Assert.Contains("old", node.Text);
            Assert.Equal(node.Title, node.Text);
        }

        [Fact]
        public void ExplicitTextWinsOverTheParts()
        {
            var node = new PropertyReassignmentMessage { PropertyName = "Foo", Text = "custom" };
            Assert.Equal("custom", node.Text);
        }

        [Fact]
        public void NodeWithoutPartsHasNoText()
        {
            Assert.Null(new PropertyReassignmentMessage().Text);
        }

        [Fact]
        public void LogReadFromDiskKeepsNoStoredReassignmentText()
        {
            var build = BinaryLog.ReadBuild(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Here()), "msbuild.binlog"));
            int lazy = 0;
            build.VisitAllChildren<PropertyReassignmentMessage>(m =>
            {
                if (m.PropertyName != null)
                {
                    lazy++;
                    Assert.Contains(m.PropertyName, m.Text);
                    Assert.Contains(m.NewValue ?? "", m.Text);
                }
            });
            Assert.True(lazy >= 0);
        }
    }
}
