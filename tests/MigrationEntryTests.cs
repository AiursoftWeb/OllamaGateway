using Aiursoft.OllamaGateway.Models.SystemViewModels;

namespace Aiursoft.OllamaGateway.Tests;

[TestClass]
public class MigrationEntryTests
{
    [TestMethod]
    public void NameReturnsMigrationNameFromStandardId()
    {
        var entry = new MigrationEntry { Id = "20260108110700_AddGlobalSettings" };

        Assert.AreEqual("AddGlobalSettings", entry.Name);
    }

    [TestMethod]
    public void AppliedAtReturnsUtcTimeFromStandardId()
    {
        var entry = new MigrationEntry { Id = "20260108110700_AddGlobalSettings" };

        Assert.AreEqual(new DateTime(2026, 1, 8, 11, 7, 0, DateTimeKind.Utc), entry.AppliedAt);
    }

    [TestMethod]
    public void AppliedAtReturnsNullForInvalidTimestamp()
    {
        var entry = new MigrationEntry { Id = "NotATimestamp_SomeMigration" };

        Assert.IsNull(entry.AppliedAt);
    }
}
