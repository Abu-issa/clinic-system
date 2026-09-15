using Clinic.Infrastructure.Authentication;

namespace Clinic.Api.Authentication;

public static class StaffLocalCommand
{
    public static async Task RunAsync(string[] args, IServiceProvider services)
    {
        if (args.Length < 2) throw new ArgumentException("A staff operation is required.");
        string Required(string name)
        {
            var index = Array.IndexOf(args, "--" + name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : throw new ArgumentException($"--{name} is required.");
        }
        using var scope = services.CreateScope();
        var admin = scope.ServiceProvider.GetRequiredService<StaffAdministration>();
        var operation = args[1];
        if (operation == "provision")
        {
            var name = Required("username");
            var doctor = Guid.Parse(Required("doctor"));
            var scopes = Required("scopes").Split(',').Select(Guid.Parse).ToArray();
            var password = ReadPassword();
            var id = await admin.ProvisionFirstDoctorAsync(name, password, doctor, scopes);
            Console.WriteLine($"Staff account ready for enrollment. Staff ID: {id}");
        }
        else
        {
            var id = Required("user-id");
            await admin.ChangeAsync(id, operation, operation == "reset-password" ? ReadPassword() : null,
                operation == "set-grants" ? Required("roles").Split(',') : null,
                operation == "set-grants" ? Required("permissions").Split(',', StringSplitOptions.RemoveEmptyEntries) : null,
                operation == "set-grants" ? Required("scopes").Split(',').Select(Guid.Parse).ToArray() : null);
            Console.WriteLine("Staff administrative change completed; affected sessions revoked.");
        }
    }

    private static string ReadPassword()
    {
        if (Console.IsInputRedirected) throw new InvalidOperationException("Use an interactive terminal for hidden password entry.");
        Console.Write("Password (hidden): ");
        var value = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (value.Length > 0) value.Length--; }
            else if (!char.IsControl(key.KeyChar)) value.Append(key.KeyChar);
        }
        Console.WriteLine();
        return value.ToString();
    }
}
