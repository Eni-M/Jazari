# Review fixture

Six small files with three planted bugs and one clean schema file, for comparing models and settings on `jazari review`:

1. `AuthService.cs`: a hardcoded admin password.
2. `OrderRepository.cs`: SQL built by string concatenation (SQL injection).
3. `RefundController.cs`: anyone signed in can refund any order (no ownership check).

`Order.cs`, `EmailSender.cs`, `Program.cs` and `StoreDbContext.cs` are clean. Findings there are false alarms. `StoreDbContext.cs` is one review part of EF Core configuration, full of words like password and encrypted, with nothing wrong in it.
