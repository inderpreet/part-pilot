using InventoryTracker.Auth;
using InventoryTracker.Data;
using InventoryTracker.Data.Entities;
using InventoryTracker.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace InventoryTracker.Tests.Services;

public class UserServiceTests : IAsyncLifetime
{
    private UserServiceTestHost _host = null!;

    public async Task InitializeAsync() => _host = await UserServiceTestHost.CreateAsync();

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private Task<Result<UserDto>> UpdateAsync(UserDto user, string? role = null, bool? isActive = null, string? displayName = null, string? email = null)
        => _host.UsersAsync(u => u.UpdateAsync(new UpdateUserInput(
            user.Id,
            displayName ?? user.DisplayName,
            email ?? user.Email,
            role ?? user.Role!,
            isActive ?? user.IsActive)));

    private Task<string?> SecurityStampAsync(string id)
        => _host.InScopeAsync(async sp => (await sp.GetRequiredService<AppDbContext>().Users.AsNoTracking().SingleAsync(u => u.Id == id)).SecurityStamp);

    private Task<IList<string>> RolesOfAsync(string id)
        => _host.InScopeAsync(async sp =>
        {
            var manager = sp.GetRequiredService<UserManager<ApplicationUser>>();
            return await manager.GetRolesAsync((await manager.FindByIdAsync(id))!);
        });

    // ---- first-run setup ------------------------------------------------------------------------------------

    [Fact]
    public async Task First_admin_is_created_with_the_Admin_role_and_flips_the_first_run_state()
    {
        var firstRun = _host.Services.GetRequiredService<IFirstRunState>();
        Assert.False(await firstRun.HasUsersAsync());

        var result = await _host.UsersAsync(u => u.CreateFirstAdminAsync("boss", "The Boss", "boss@example.test", "long-enough-pw"));

        Assert.True(result.Succeeded);
        Assert.Equal(Roles.Admin, result.Value.Role);
        Assert.True(result.Value.IsActive);
        Assert.Equal(["Admin"], await RolesOfAsync(result.Value.Id));
        Assert.True(await firstRun.HasUsersAsync());
    }

    [Fact]
    public async Task First_admin_cannot_be_created_twice()
    {
        await _host.UsersAsync(u => u.CreateFirstAdminAsync("boss", "The Boss", null, "long-enough-pw"));

        var second = await _host.UsersAsync(u => u.CreateFirstAdminAsync("intruder", "Intruder", null, "long-enough-pw"));

        Assert.Equal("setup.complete", Assert.Single(second.Errors).Code);
        Assert.Null(await _host.InScopeAsync(sp => sp.GetRequiredService<AppDbContext>().Users.SingleOrDefaultAsync(u => u.UserName == "intruder")));
    }

    [Fact]
    public async Task Concurrent_first_admin_requests_create_exactly_one_user()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(i =>
            _host.UsersAsync(u => u.CreateFirstAdminAsync($"racer{i}", "Racer", null, "long-enough-pw"))));

        Assert.Single(results, r => r.Succeeded);
        Assert.Equal(1, await _host.InScopeAsync(sp => sp.GetRequiredService<AppDbContext>().Users.CountAsync()));
    }

    [Fact]
    public async Task A_rejected_first_admin_leaves_no_user_behind()
    {
        var result = await _host.UsersAsync(u => u.CreateFirstAdminAsync("boss", "The Boss", null, "short"));

        Assert.False(result.Succeeded);
        Assert.Equal(0, await _host.InScopeAsync(sp => sp.GetRequiredService<AppDbContext>().Users.CountAsync()));
        Assert.False(await _host.Services.GetRequiredService<IFirstRunState>().HasUsersAsync());
    }

    // ---- create -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_stores_the_user_with_exactly_one_role()
    {
        var created = await _host.NewUserAsync("alice", Roles.Staff);

        Assert.Equal(Roles.Staff, created.Role);
        Assert.Equal(["Staff"], await RolesOfAsync(created.Id));
        var fetched = await _host.UsersAsync(u => u.GetAsync(created.Id));
        Assert.Equal(created.UserName, fetched!.UserName);
        Assert.Equal(Roles.Staff, fetched.Role);
        Assert.Null(fetched.LastSignInAt);
    }

    [Fact]
    public async Task Create_can_make_an_inactive_user()
    {
        var created = await _host.NewUserAsync("dormant", isActive: false);

        Assert.False(created.IsActive);
        Assert.False((await _host.UsersAsync(u => u.GetAsync(created.Id)))!.IsActive);
    }

    [Fact]
    public async Task User_names_are_unique_ignoring_case()
    {
        await _host.NewUserAsync("Alice");

        var result = await _host.UsersAsync(u => u.CreateAsync(new CreateUserInput("ALICE", "Other", null, Roles.Viewer, "password-1234")));

        Assert.False(result.Succeeded);
        Assert.Equal(UserFields.UserName, result.Errors.Single().Field);
        Assert.Equal(1, await _host.InScopeAsync(sp => sp.GetRequiredService<AppDbContext>().Users.CountAsync()));
    }

    [Fact]
    public async Task Login_lookup_is_case_insensitive()
    {
        var created = await _host.NewUserAsync("Alice");

        var found = await _host.InScopeAsync(sp => sp.GetRequiredService<UserManager<ApplicationUser>>().FindByNameAsync("aLiCe"));

        Assert.Equal(created.Id, found!.Id);
    }

    [Theory]
    [InlineData("", "Name", null, "Staff", UserFields.UserName)]
    [InlineData("   ", "Name", null, "Staff", UserFields.UserName)]
    [InlineData("bob", "", null, "Staff", UserFields.DisplayName)]
    [InlineData("bob", "Name", "not-an-email", "Staff", UserFields.Email)]
    [InlineData("bob", "Name", null, "Root", UserFields.Role)]
    [InlineData("bob", "Name", null, "", UserFields.Role)]
    [InlineData("bob smith", "Name", null, "Staff", UserFields.UserName)] // space is not an allowed user name character
    public async Task Create_validates_input_and_attaches_errors_to_fields(string userName, string displayName, string? email, string role, string field)
    {
        var result = await _host.UsersAsync(u => u.CreateAsync(new CreateUserInput(userName, displayName, email, role, "password-1234")));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Field == field);
        Assert.Equal(0, await _host.InScopeAsync(sp => sp.GetRequiredService<AppDbContext>().Users.CountAsync()));
    }

    [Fact]
    public async Task Create_limits_user_name_and_display_name_length()
    {
        var result = await _host.UsersAsync(u => u.CreateAsync(new CreateUserInput(
            new string('u', 51), new string('d', ApplicationUser.DisplayNameMaxLength + 1), null, Roles.Staff, "password-1234")));

        Assert.Contains(result.Errors, e => e.Field == UserFields.UserName);
        Assert.Contains(result.Errors, e => e.Field == UserFields.DisplayName);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("1234567")]
    public async Task Password_must_be_at_least_eight_characters(string password)
    {
        var result = await _host.UsersAsync(u => u.CreateAsync(new CreateUserInput("bob", "Bob", null, Roles.Staff, password)));

        Assert.Equal(UserFields.Password, Assert.Single(result.Errors).Field);
    }

    [Theory]
    [InlineData("alllowercase")] // no complexity rules (NIST-style)
    [InlineData("12345678")]
    [InlineData("a b c d e f")]
    public async Task Password_has_no_complexity_rules(string password)
    {
        var result = await _host.UsersAsync(u => u.CreateAsync(new CreateUserInput("bob", "Bob", null, Roles.Staff, password)));

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Password_length_is_capped()
    {
        var result = await _host.UsersAsync(u => u.CreateAsync(new CreateUserInput("bob", "Bob", null, Roles.Staff, new string('p', PasswordLengthValidator.MaxLength + 1))));

        Assert.Equal(UserFields.Password, Assert.Single(result.Errors).Field);
    }

    [Fact]
    public async Task Email_is_optional_and_blank_is_stored_as_null()
    {
        var result = await _host.UsersAsync(u => u.CreateAsync(new CreateUserInput("bob", "Bob", "   ", Roles.Staff, "password-1234")));

        Assert.True(result.Succeeded);
        Assert.Null(result.Value.Email);
    }

    [Fact]
    public async Task The_same_email_may_be_used_by_two_users()
    {
        await _host.UsersAsync(u => u.CreateAsync(new CreateUserInput("bob", "Bob", "shared@example.test", Roles.Staff, "password-1234")));

        var second = await _host.UsersAsync(u => u.CreateAsync(new CreateUserInput("carol", "Carol", "shared@example.test", Roles.Staff, "password-1234")));

        Assert.True(second.Succeeded);
    }

    // ---- update -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Update_changes_profile_and_replaces_the_role()
    {
        var user = await _host.NewUserAsync("alice", Roles.Staff);

        var result = await UpdateAsync(user, role: Roles.Viewer, displayName: "Alice A.", email: "a@example.test");

        Assert.True(result.Succeeded);
        var fetched = (await _host.UsersAsync(u => u.GetAsync(user.Id)))!;
        Assert.Equal("Alice A.", fetched.DisplayName);
        Assert.Equal("a@example.test", fetched.Email);
        Assert.Equal(Roles.Viewer, fetched.Role);
        Assert.Equal(["Viewer"], await RolesOfAsync(user.Id)); // exactly one role
    }

    [Fact]
    public async Task Update_collapses_a_user_that_somehow_has_several_roles_to_one()
    {
        var user = await _host.NewUserAsync("alice", Roles.Staff);
        await _host.InScopeAsync(async sp =>
        {
            var manager = sp.GetRequiredService<UserManager<ApplicationUser>>();
            await manager.AddToRoleAsync((await manager.FindByIdAsync(user.Id))!, Roles.Viewer);
        });
        Assert.Equal(2, (await RolesOfAsync(user.Id)).Count);

        var result = await UpdateAsync(user, role: Roles.Staff);

        Assert.True(result.Succeeded);
        Assert.Equal(["Staff"], await RolesOfAsync(user.Id));
    }

    [Fact]
    public async Task Update_cannot_change_the_user_name()
    {
        var user = await _host.NewUserAsync("alice");

        await UpdateAsync(user, displayName: "Renamed");

        Assert.Equal("alice", (await _host.UsersAsync(u => u.GetAsync(user.Id)))!.UserName);
    }

    [Fact]
    public async Task Update_unknown_user_fails()
    {
        var result = await _host.UsersAsync(u => u.UpdateAsync(new UpdateUserInput("nope", "X", null, Roles.Staff, true)));

        Assert.Equal("user.not_found", Assert.Single(result.Errors).Code);
    }

    [Theory]
    [InlineData("", "bad-role", UserFields.Role)]
    [InlineData(" ", "Staff", UserFields.DisplayName)]
    public async Task Update_validates_input(string displayName, string role, string field)
    {
        var user = await _host.NewUserAsync("alice");

        var result = await _host.UsersAsync(u => u.UpdateAsync(new UpdateUserInput(user.Id, displayName, null, role, true)));

        Assert.Contains(result.Errors, e => e.Field == field);
    }

    [Fact]
    public async Task Role_change_and_deactivation_rotate_the_security_stamp_but_a_plain_edit_does_not()
    {
        var user = await _host.NewUserAsync("alice", Roles.Staff);
        var stamp0 = await SecurityStampAsync(user.Id);

        await UpdateAsync(user, displayName: "Alice Renamed");
        Assert.Equal(stamp0, await SecurityStampAsync(user.Id));

        await UpdateAsync(user, role: Roles.Viewer);
        var stamp1 = await SecurityStampAsync(user.Id);
        Assert.NotEqual(stamp0, stamp1);

        await UpdateAsync(user with { Role = Roles.Viewer }, isActive: false);
        Assert.NotEqual(stamp1, await SecurityStampAsync(user.Id));
    }

    // ---- last-admin guard ---------------------------------------------------------------------------------------

    [Fact]
    public async Task The_last_active_admin_cannot_be_deactivated()
    {
        var admin = await _host.NewUserAsync("root", Roles.Admin);
        await _host.NewUserAsync("staffer", Roles.Staff);

        var result = await UpdateAsync(admin, isActive: false);

        Assert.False(result.Succeeded);
        var error = Assert.Single(result.Errors);
        Assert.Equal("user.last_admin", error.Code);
        Assert.Equal(UserFields.IsActive, error.Field);
        Assert.True((await _host.UsersAsync(u => u.GetAsync(admin.Id)))!.IsActive);
    }

    [Fact]
    public async Task The_last_active_admin_cannot_be_demoted()
    {
        var admin = await _host.NewUserAsync("root", Roles.Admin);

        var result = await UpdateAsync(admin, role: Roles.Staff);

        var error = Assert.Single(result.Errors);
        Assert.Equal("user.last_admin", error.Code);
        Assert.Equal(UserFields.Role, error.Field);
        Assert.Equal(Roles.Admin, (await _host.UsersAsync(u => u.GetAsync(admin.Id)))!.Role);
        Assert.Equal(["Admin"], await RolesOfAsync(admin.Id));
    }

    [Fact]
    public async Task Demoting_and_deactivating_together_is_also_blocked()
    {
        var admin = await _host.NewUserAsync("root", Roles.Admin);

        var result = await UpdateAsync(admin, role: Roles.Viewer, isActive: false);

        Assert.Equal("user.last_admin", Assert.Single(result.Errors).Code);
    }

    [Fact]
    public async Task An_admin_can_be_demoted_or_deactivated_while_another_active_admin_exists()
    {
        var first = await _host.NewUserAsync("root", Roles.Admin);
        var second = await _host.NewUserAsync("deputy", Roles.Admin);

        Assert.True((await UpdateAsync(first, role: Roles.Staff)).Succeeded);

        // `second` is now the last one.
        Assert.False((await UpdateAsync(second, isActive: false)).Succeeded);
    }

    [Fact]
    public async Task An_inactive_admin_does_not_count_as_another_admin()
    {
        var active = await _host.NewUserAsync("root", Roles.Admin);
        await _host.NewUserAsync("retired", Roles.Admin, isActive: false);

        var result = await UpdateAsync(active, role: Roles.Staff);

        Assert.Equal("user.last_admin", Assert.Single(result.Errors).Code);
    }

    [Fact]
    public async Task Editing_the_last_admin_without_demoting_or_deactivating_is_fine()
    {
        var admin = await _host.NewUserAsync("root", Roles.Admin);

        var result = await UpdateAsync(admin, displayName: "Root Admin", email: "root@example.test");

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task An_inactive_admin_can_be_reactivated_or_demoted_freely()
    {
        await _host.NewUserAsync("root", Roles.Admin);
        var retired = await _host.NewUserAsync("retired", Roles.Admin, isActive: false);

        Assert.True((await UpdateAsync(retired, role: Roles.Viewer)).Succeeded);
    }

    [Fact]
    public async Task Two_admins_demoting_each_other_at_once_cannot_leave_zero_admins()
    {
        var a = await _host.NewUserAsync("alpha", Roles.Admin);
        var b = await _host.NewUserAsync("bravo", Roles.Admin);

        var results = await Task.WhenAll(UpdateAsync(a, role: Roles.Staff), UpdateAsync(b, role: Roles.Staff));

        Assert.Single(results, r => r.Succeeded);
        var activeAdmins = await _host.InScopeAsync(sp => sp.GetRequiredService<AppDbContext>().UserRoles
            .CountAsync(ur => ur.RoleId == InventoryTracker.Data.Configurations.RoleConfiguration.AdminId));
        Assert.Equal(1, activeAdmins);
    }

    // ---- passwords -----------------------------------------------------------------------------------------

    private Task<bool> PasswordWorksAsync(string id, string password)
        => _host.InScopeAsync(async sp =>
        {
            var manager = sp.GetRequiredService<UserManager<ApplicationUser>>();
            return await manager.CheckPasswordAsync((await manager.FindByIdAsync(id))!, password);
        });

    [Fact]
    public async Task Reset_password_sets_the_new_password_and_rotates_the_stamp()
    {
        var user = await _host.NewUserAsync("alice", password: "old-password-1");
        var stamp = await SecurityStampAsync(user.Id);

        var result = await _host.UsersAsync(u => u.ResetPasswordAsync(user.Id, "brand-new-pw-2", "actor-id"));

        Assert.True(result.Succeeded);
        Assert.True(await PasswordWorksAsync(user.Id, "brand-new-pw-2"));
        Assert.False(await PasswordWorksAsync(user.Id, "old-password-1"));
        Assert.NotEqual(stamp, await SecurityStampAsync(user.Id));
    }

    [Fact]
    public async Task Reset_password_clears_a_lockout()
    {
        var user = await _host.NewUserAsync("alice");
        await _host.InScopeAsync(async sp =>
        {
            var manager = sp.GetRequiredService<UserManager<ApplicationUser>>();
            var entity = (await manager.FindByIdAsync(user.Id))!;
            await manager.SetLockoutEndDateAsync(entity, DateTimeOffset.UtcNow.AddMinutes(10));
            await manager.AccessFailedAsync(entity);
        });

        await _host.UsersAsync(u => u.ResetPasswordAsync(user.Id, "brand-new-pw-2"));

        await _host.InScopeAsync(async sp =>
        {
            var manager = sp.GetRequiredService<UserManager<ApplicationUser>>();
            var entity = (await manager.FindByIdAsync(user.Id))!;
            Assert.False(await manager.IsLockedOutAsync(entity));
            Assert.Equal(0, await manager.GetAccessFailedCountAsync(entity));
        });
    }

    [Fact]
    public async Task Reset_password_rejects_weak_passwords_and_unknown_users()
    {
        var user = await _host.NewUserAsync("alice", password: "old-password-1");

        var weak = await _host.UsersAsync(u => u.ResetPasswordAsync(user.Id, "short"));
        var unknown = await _host.UsersAsync(u => u.ResetPasswordAsync("nope", "long-enough-pw"));

        Assert.Equal(UserFields.NewPassword, Assert.Single(weak.Errors).Field);
        Assert.Equal("user.not_found", Assert.Single(unknown.Errors).Code);
        Assert.True(await PasswordWorksAsync(user.Id, "old-password-1"));
    }

    [Fact]
    public async Task Change_own_password_requires_the_current_password()
    {
        var user = await _host.NewUserAsync("alice", password: "old-password-1");

        var wrong = await _host.UsersAsync(u => u.ChangeOwnPasswordAsync(user.Id, "not-my-password", "brand-new-pw-2"));
        Assert.Equal(UserFields.CurrentPassword, Assert.Single(wrong.Errors).Field);
        Assert.True(await PasswordWorksAsync(user.Id, "old-password-1"));

        var ok = await _host.UsersAsync(u => u.ChangeOwnPasswordAsync(user.Id, "old-password-1", "brand-new-pw-2"));
        Assert.True(ok.Succeeded);
        Assert.True(await PasswordWorksAsync(user.Id, "brand-new-pw-2"));
    }

    [Fact]
    public async Task Change_own_password_enforces_the_policy_on_the_new_password()
    {
        var user = await _host.NewUserAsync("alice", password: "old-password-1");

        var result = await _host.UsersAsync(u => u.ChangeOwnPasswordAsync(user.Id, "old-password-1", "short"));

        Assert.Equal(UserFields.NewPassword, Assert.Single(result.Errors).Field);
        Assert.True(await PasswordWorksAsync(user.Id, "old-password-1"));
    }

    // ---- sign-in bookkeeping ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Record_sign_in_stores_the_utc_time_without_touching_the_concurrency_stamp()
    {
        var user = await _host.NewUserAsync("alice");
        string? before = await _host.InScopeAsync(async sp => (await sp.GetRequiredService<AppDbContext>().Users.AsNoTracking().SingleAsync(u => u.Id == user.Id)).ConcurrencyStamp);

        _host.Clock.UtcNow = new DateTime(2026, 11, 8, 9, 30, 0, DateTimeKind.Utc);
        await _host.UsersAsync(async u =>
        {
            await u.RecordSignInAsync(user.Id);
            return 0;
        });

        var after = await _host.InScopeAsync(sp => sp.GetRequiredService<AppDbContext>().Users.AsNoTracking().SingleAsync(u => u.Id == user.Id));
        Assert.Equal(new DateTime(2026, 11, 8, 9, 30, 0, DateTimeKind.Utc), after.LastSignInAt);
        Assert.Equal(DateTimeKind.Utc, after.LastSignInAt!.Value.Kind);
        Assert.Equal(before, after.ConcurrencyStamp);
    }

    // ---- listing ---------------------------------------------------------------------------------------

    private async Task SeedListAsync()
    {
        await _host.NewUserAsync("anna", Roles.Admin);
        await _host.NewUserAsync("bert", Roles.Staff);
        await _host.NewUserAsync("cara", Roles.Viewer);
        await _host.NewUserAsync("dave", Roles.Staff, isActive: false);
    }

    [Fact]
    public async Task List_returns_users_sorted_by_user_name_with_their_role()
    {
        await SeedListAsync();

        var page = await _host.UsersAsync(u => u.ListAsync(new ListQuery()));

        Assert.Equal(["anna", "bert", "cara", "dave"], page.Items.Select(i => i.UserName));
        Assert.Equal([Roles.Admin, Roles.Staff, Roles.Viewer, Roles.Staff], page.Items.Select(i => i.Role));
        Assert.Equal(4, page.TotalCount);
        Assert.False(page.Items.Single(i => i.UserName == "dave").IsActive);
    }

    [Theory]
    [InlineData("ber", new[] { "bert" })]
    [InlineData("USER CARA", new[] { "cara" })] // display name, case-insensitive
    [InlineData("dave@example", new[] { "dave" })] // email
    [InlineData("zzz", new string[0])]
    public async Task List_search_matches_user_name_display_name_and_email(string search, string[] expected)
    {
        await SeedListAsync();

        var page = await _host.UsersAsync(u => u.ListAsync(new ListQuery { Search = search }));

        Assert.Equal(expected, page.Items.Select(i => i.UserName));
    }

    [Theory]
    [InlineData("%")]
    [InlineData("_")]
    [InlineData("\\")]
    public async Task List_search_treats_like_wildcards_literally(string search)
    {
        await SeedListAsync();

        var page = await _host.UsersAsync(u => u.ListAsync(new ListQuery { Search = search }));

        Assert.Empty(page.Items);
    }

    [Theory]
    [InlineData("name", false, new[] { "anna", "bert", "cara", "dave" })]
    [InlineData("name", true, new[] { "dave", "cara", "bert", "anna" })]
    [InlineData("role", false, new[] { "anna", "bert", "dave", "cara" })]
    [InlineData("active", true, new[] { "anna", "bert", "cara", "dave" })]
    [InlineData(null, true, new[] { "dave", "cara", "bert", "anna" })]
    public async Task List_sorts_by_the_requested_column(string? sort, bool desc, string[] expected)
    {
        await SeedListAsync();

        var page = await _host.UsersAsync(u => u.ListAsync(new ListQuery { Sort = sort, Desc = desc }));

        Assert.Equal(expected, page.Items.Select(i => i.UserName));
    }

    [Fact]
    public async Task List_is_paged()
    {
        await SeedListAsync();

        var second = await _host.UsersAsync(u => u.ListAsync(new ListQuery { Page = 2, PageSize = 3 }));

        Assert.Equal(["dave"], second.Items.Select(i => i.UserName));
        Assert.Equal(4, second.TotalCount);
        Assert.Equal(2, second.TotalPages);
    }

    [Fact]
    public async Task List_includes_users_without_a_role()
    {
        await _host.InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            db.Users.Add(new ApplicationUser { UserName = "orphan", NormalizedUserName = "ORPHAN", DisplayName = "Orphan", SecurityStamp = "x" });
            await db.SaveChangesAsync();
        });

        var page = await _host.UsersAsync(u => u.ListAsync(new ListQuery()));

        var orphan = Assert.Single(page.Items);
        Assert.Null(orphan.Role);
    }

    [Fact]
    public async Task Get_returns_null_for_an_unknown_id()
    {
        Assert.Null(await _host.UsersAsync(u => u.GetAsync("nope")));
    }

    // ---- events ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Account_changes_publish_events_with_the_actor_and_no_secrets()
    {
        var created = (await _host.UsersAsync(u => u.CreateAsync(
            new CreateUserInput("alice", "Alice", null, Roles.Staff, "password-1234"), "admin-1"))).Value;
        await UpdateAsync(created, role: Roles.Viewer);
        await _host.UsersAsync(u => u.ResetPasswordAsync(created.Id, "brand-new-pw-2", "admin-1"));
        await _host.UsersAsync(u => u.ChangeOwnPasswordAsync(created.Id, "brand-new-pw-2", "another-new-pw-3"));

        Assert.Equal(
            [UserEventType.UserCreated, UserEventType.UserUpdated, UserEventType.PasswordReset, UserEventType.PasswordChanged],
            _host.Events.Events.Select(e => e.Type));
        Assert.Equal("admin-1", _host.Events.Events[0].ActorUserId);
        Assert.Equal("alice", _host.Events.Events[0].UserName);
        Assert.Contains("Staff", _host.Events.Events[1].Detail);
        Assert.Contains("Viewer", _host.Events.Events[1].Detail);
        Assert.Equal(_host.Clock.UtcNow, _host.Events.Events[0].OccurredAtUtc);
        Assert.DoesNotContain(_host.Events.Events, e => (e.Detail ?? "").Contains("password-1234") || (e.Detail ?? "").Contains("brand-new"));
    }

    [Fact]
    public async Task A_failing_event_handler_does_not_break_the_operation()
    {
        _host.Events.Throw = true;

        var result = await _host.UsersAsync(u => u.CreateAsync(new CreateUserInput("alice", "Alice", null, Roles.Staff, "password-1234")));

        Assert.True(result.Succeeded);
        Assert.Single(_host.Events.Events);
    }

    [Fact]
    public async Task Failed_operations_publish_no_events()
    {
        await _host.UsersAsync(u => u.CreateAsync(new CreateUserInput("alice", "Alice", null, "NoSuchRole", "password-1234")));

        Assert.Empty(_host.Events.Events);
    }
}
