using MemoDesk.Vault.Core;
using MemoDesk.Vault.Infrastructure;

namespace MemoDesk.Vault.Tests;

public sealed class MasterPasswordPolicyTests
{
    [Fact]
    public void EmptyPasswordExplainsThatInputIsRequired() =>
        Assert.Equal("请输入主密码。", MasterPasswordPolicy.GetError("", creating: true));

    [Fact]
    public void ShortPasswordExplainsCurrentLengthAndMissingCharacters() =>
        Assert.Equal("主密码至少需要 10 个字符，目前只有 5 个，还差 5 个。", MasterPasswordPolicy.GetError("abcde", creating: true));

    [Theory]
    [InlineData(9, false)]
    [InlineData(10, true)]
    [InlineData(11, true)]
    public void NewPasswordMinimumIsTenCharacters(int length, bool accepted) =>
        Assert.Equal(accepted, MasterPasswordPolicy.GetError(new string('a', length), creating: true) is null);

    [Fact]
    public void LongPasswordExplainsTheLimitAndExcessCharacters() =>
        Assert.Equal("主密码最多支持 1024 个字符，目前为 1025 个，请减少 1 个。", MasterPasswordPolicy.GetError(new string('a', 1025), creating: true));

    [Theory]
    [InlineData("abcdefghijkl")]
    [InlineData("一个支持中文和空格的长口令")]
    [InlineData("  示例口令 abcdef  ")]
    public void ValidPasswordsDoNotRequireSpecificCharacterClasses(string password) =>
        Assert.Null(MasterPasswordPolicy.GetError(password, creating: true));

    [Fact]
    public void MaximumLengthIsAcceptedWithoutTruncation() =>
        Assert.Null(MasterPasswordPolicy.GetError(new string('a', 1024), creating: true));

    [Fact]
    public void UnlockDoesNotApplyNewPasswordMinimumLength() =>
        Assert.Null(MasterPasswordPolicy.GetError("short", creating: false));

    [Fact]
    public void BackendDistinguishesInvalidFormatAndDoesNotCreateVault()
    {
        var root = Path.Combine(Path.GetTempPath(), "MemoDesk.Vault.Tests", Guid.NewGuid().ToString("N"));
        using var vault = new VaultService(Path.Combine(root, "vault.db"));
        try
        {
            var error = Assert.Throws<VaultException>(() => vault.Create("short"));
            Assert.Equal(VaultError.InvalidPasswordFormat, error.Error);
            Assert.False(vault.Exists);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void TenCharacterPasswordCanCreateReopenAndChangeMasterPassword()
    {
        var root = Path.Combine(Path.GetTempPath(), "MemoDesk.Vault.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(root, "vault.db");
            using (var vault = new VaultService(path))
            {
                vault.Create("1234567890");
                using var entry = new VaultEntryDraft { Title = "Synthetic ten-character master", Password = "Synthetic secret" };
                vault.Save(entry);
            }
            using (var vault = new VaultService(path))
            {
                vault.Unlock("1234567890");
                vault.ChangeMasterPassword("1234567890", "abcdefghij");
                vault.Lock();
                Assert.Equal(VaultError.InvalidPassword, Assert.Throws<VaultException>(() => vault.Unlock("1234567890")).Error);
                vault.Unlock("abcdefghij");
                var row = Assert.Single(vault.List());
                using var secret = vault.ReadSecret(row.Id, row.RowVersion);
                Assert.Equal("Synthetic secret", secret.Password);
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
