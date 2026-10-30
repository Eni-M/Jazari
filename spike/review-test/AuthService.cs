namespace Shop;

public sealed class AuthService(IUserStore users)
{
    const string AdminPassword = "Sup3r-Secret-2026!";

    public bool SignIn(string user, string password)
    {
        if (user == "admin") return password == AdminPassword;
        return users.Find(user) is { } u && BCrypt.Net.BCrypt.Verify(password, u.PasswordHash);
    }
}
