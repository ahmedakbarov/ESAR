using System.Linq.Expressions;
using Esar.Application.Abstractions;
using Esar.Application.Auditing;
using Esar.Application.Auth;
using Esar.Application.Matching;
using Esar.Domain.Entities;
using Esar.Domain.Enums;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Esar.UnitTests;

public class AuthSecuritySettingsTests
{
    private readonly Mock<IUnitOfWork> _uow = new();
    private readonly Mock<IRepository<User>> _users = new();
    private readonly Mock<IRepository<Role>> _roles = new();
    private readonly Mock<IRepository<Permission>> _permissions = new();
    private readonly Mock<IRepository<Setting>> _settings = new();
    private readonly Mock<IPasswordHasher> _hasher = new();
    private readonly Mock<IJwtTokenService> _jwt = new();
    private readonly Mock<IAuditService> _audit = new();
    private readonly Mock<ILdapLoginService> _ldap = new();
    private readonly Mock<IEntraTokenValidator> _entra = new();
    private readonly List<Setting> _settingRows = new();
    private User? _user;

    private AuthService Sut => new(
        _uow.Object,
        _hasher.Object,
        _jwt.Object,
        _audit.Object,
        _ldap.Object,
        _entra.Object,
        NullLogger<AuthService>.Instance);

    public AuthSecuritySettingsTests()
    {
        _uow.SetupGet(u => u.Users).Returns(_users.Object);
        _uow.SetupGet(u => u.Roles).Returns(_roles.Object);
        _uow.SetupGet(u => u.Permissions).Returns(_permissions.Object);
        _uow.SetupGet(u => u.Settings).Returns(_settings.Object);
        _uow.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        _users.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<User, bool>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Expression<Func<User, bool>> predicate, CancellationToken _) =>
                _user is not null && predicate.Compile()(_user) ? _user : null);
        _users.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) => _user?.Id == id ? _user : null);
        _settings.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<Setting, bool>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Expression<Func<Setting, bool>> predicate, CancellationToken _) =>
                _settingRows.FirstOrDefault(predicate.Compile()));
        _roles.Setup(r => r.ListAsync(It.IsAny<Expression<Func<Role, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Role>());
        _permissions.Setup(r => r.ListAsync(It.IsAny<Expression<Func<Permission, bool>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Permission>());
        _audit.Setup(a => a.LogAsync(It.IsAny<AuditAction>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<object?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    [Fact]
    public async Task Login_locks_local_user_when_configured_failed_attempt_limit_is_reached()
    {
        _user = LocalUser();
        Set(SettingKeys.SecurityLoginMaxFailedAttempts, "2");
        Set(SettingKeys.SecurityLoginLockoutMinutes, "9");
        _hasher.Setup(h => h.Verify("bad-password", _user.PasswordHash!)).Returns(false);

        var first = await Sut.LoginAsync(_user.Username, "bad-password");
        var second = await Sut.LoginAsync(_user.Username, "bad-password");

        first.Error.Should().Be("Invalid credentials");
        second.Error.Should().Be("Account temporarily locked");
        _user.LockedOutUntil.Should().BeAfter(DateTime.UtcNow.AddMinutes(8));
        _user.LockedOutUntil.Should().BeBefore(DateTime.UtcNow.AddMinutes(10));
        _user.FailedLoginAttempts.Should().Be(0);
    }

    [Fact]
    public async Task ChangePassword_uses_configured_minimum_password_length()
    {
        _user = LocalUser();
        Set(SettingKeys.SecurityPasswordMinLength, "16");
        _hasher.Setup(h => h.Verify("current-password", _user.PasswordHash!)).Returns(true);

        var result = await Sut.ChangePasswordAsync(_user.Id, "current-password", "too-short");

        result.Success.Should().BeFalse();
        result.Error.Should().Be("Password must be at least 16 characters");
        _hasher.Verify(h => h.Hash(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Login_passes_configured_token_lifetime_to_jwt_service()
    {
        _user = LocalUser();
        Set(SettingKeys.SecuritySessionTokenLifetimeMinutes, "7");
        _hasher.Setup(h => h.Verify("correct-password", _user.PasswordHash!)).Returns(true);
        _jwt.Setup(j => j.CreateToken(_user, It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<IReadOnlyCollection<string>>(), 7))
            .Returns(("token", DateTime.UtcNow.AddMinutes(7)));

        var result = await Sut.LoginAsync(_user.Username, "correct-password");

        result.Success.Should().BeTrue();
        result.Token.Should().Be("token");
        _jwt.Verify(j => j.CreateToken(_user, It.IsAny<IReadOnlyCollection<string>>(),
            It.IsAny<IReadOnlyCollection<string>>(), 7), Times.Once);
    }

    private static User LocalUser() => new()
    {
        Id = Guid.NewGuid(),
        Username = "local.user",
        Email = "local.user@esar.local",
        DisplayName = "Local User",
        PasswordHash = "hash",
        AuthProvider = AuthProvider.Local,
        IsActive = true
    };

    private void Set(string key, string value) =>
        _settingRows.Add(new Setting { Key = key, Value = value, Description = key });
}
