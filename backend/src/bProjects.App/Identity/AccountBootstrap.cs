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
/// Gli Admin sono solo quelli elencati in <c>Bootstrap:Admins</c>: un utente nuovo nasce con il solo ruolo Admin; uno esistente
/// riceve il ruolo Admin (nessuna modifica a password o ruolo PM). Nessun ruolo è deciso dal client.
/// </summary>
public static class AccountBootstrap
{
    public const string PmRole = "PM";

    /// <summary>Ruolo riservato ai limiti dei documenti (T005). Un Admin non è un PM: non ottiene l'accesso ai progetti.</summary>
    public const string AdminRole = "Admin";
    public const string AdminPolicy = "Admin";

    public static async Task<int> RunAsync(IServiceProvider services, IConfiguration configuration)
    {
        var accounts = configuration.GetSection("Bootstrap:Accounts").Get<List<BootstrapAccount>>() ?? [];
        var admins = configuration.GetSection("Bootstrap:Admins").Get<List<BootstrapAccount>>() ?? [];
        if ((accounts.Count == 0 && admins.Count == 0) || accounts.Any(a => a.UserName.Length == 0 || a.Password.Length == 0) || admins.Any(a => a.UserName.Length == 0))
        {
            throw new InvalidOperationException("Configurare Bootstrap:Accounts con UserName e Password per ogni PM (e Bootstrap:Admins con UserName per ogni Admin).");
        }

        using var scope = services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser<Guid>>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();

        if (!await roles.RoleExistsAsync(PmRole))
        {
            Throw(await roles.CreateAsync(new IdentityRole<Guid>(PmRole)));
        }

        if (admins.Count > 0 && !await roles.RoleExistsAsync(AdminRole))
        {
            Throw(await roles.CreateAsync(new IdentityRole<Guid>(AdminRole)));
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

        foreach (var admin in admins)
        {
            var user = await users.FindByNameAsync(admin.UserName);
            if (user is null)
            {
                if (admin.Password.Length == 0)
                {
                    throw new InvalidOperationException("Bootstrap:Admins: password obbligatoria per un Admin nuovo.");
                }

                user = new IdentityUser<Guid> { Id = Guid.NewGuid(), UserName = admin.UserName };
                Throw(await users.CreateAsync(user, admin.Password));
                Throw(await users.AddToRoleAsync(user, AdminRole));
                created++;
            }
            else if (!await users.IsInRoleAsync(user, AdminRole))
            {
                Throw(await users.AddToRoleAsync(user, AdminRole)); // elevazione esplicita da configurazione; password e ruolo PM invariati
                created++;
            }
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
