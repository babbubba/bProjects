using Microsoft.AspNetCore.Identity;

namespace bProjects.App.Identity;

public sealed class BootstrapAccount
{
    public string UserName { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

/// <summary>
/// Creazione esplicita e non distruttiva degli account PM (<c>bootstrap-accounts</c>): gli account
/// esistenti non vengono toccati e non esiste alcun signup pubblico. Le credenziali arrivano
/// dalla configurazione (variabili d'ambiente <c>Bootstrap__Accounts__N__UserName/Password</c>).
/// </summary>
public static class AccountBootstrap
{
    public const string PmRole = "PM";

    public static async Task<int> RunAsync(IServiceProvider services, IConfiguration configuration)
    {
        var accounts = configuration.GetSection("Bootstrap:Accounts").Get<List<BootstrapAccount>>() ?? [];
        if (accounts.Count == 0 || accounts.Any(a => a.UserName.Length == 0 || a.Password.Length == 0))
        {
            throw new InvalidOperationException("Configurare Bootstrap:Accounts con UserName e Password per ogni PM.");
        }

        using var scope = services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser<Guid>>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();

        if (!await roles.RoleExistsAsync(PmRole))
        {
            Throw(await roles.CreateAsync(new IdentityRole<Guid>(PmRole)));
        }

        var created = 0;
        foreach (var account in accounts)
        {
            if (await users.FindByNameAsync(account.UserName) is not null)
            {
                continue;
            }

            var user = new IdentityUser<Guid> { Id = Guid.NewGuid(), UserName = account.UserName };
            Throw(await users.CreateAsync(user, account.Password));
            Throw(await users.AddToRoleAsync(user, PmRole));
            created++;
        }

        return created;
    }

    private static void Throw(IdentityResult result)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException("Bootstrap account non riuscito: " + string.Join("; ", result.Errors.Select(e => e.Code)));
        }
    }
}
