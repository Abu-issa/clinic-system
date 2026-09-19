using Clinic.Application.Abstractions;
using Clinic.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Clinic.Infrastructure.Authentication;

public static class StaffIdentityRegistration
{
    public static IServiceCollection AddStaffIdentity(this IServiceCollection services)
    {
        services.AddIdentityCore<StaffUser>(options =>
        {
            options.Password.RequiredLength = 12;
            options.Lockout.AllowedForNewUsers = true;
            options.Lockout.MaxFailedAccessAttempts = 5;
            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        }).AddRoles<IdentityRole>().AddEntityFrameworkStores<ClinicDbContext>().AddDefaultTokenProviders()
            .AddPasswordValidator<StaffPasswordLengthValidator>();
        services.AddScoped<StaffAuthentication>();
        services.AddScoped<IStaffAuthentication>(sp => sp.GetRequiredService<StaffAuthentication>());
        services.AddScoped<MobileStaffAuthentication>();
        services.AddScoped<StaffAdministration>();
        return services;
    }
}

// Provisioning and administrative reset must not persist a password the HTTP login rejects.
internal sealed class StaffPasswordLengthValidator : IPasswordValidator<StaffUser>
{
    public Task<IdentityResult> ValidateAsync(UserManager<StaffUser> manager, StaffUser user, string? password) =>
        Task.FromResult(password is { Length: > 1024 }
            ? IdentityResult.Failed(new IdentityError { Code = "PasswordTooLong", Description = "Passwords must not exceed 1024 characters." })
            : IdentityResult.Success);
}
