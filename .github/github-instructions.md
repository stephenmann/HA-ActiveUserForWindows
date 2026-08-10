# C#/.NET Development Instructions

## Purpose

This repository follows modern .NET engineering practices with a strong focus on:

- Clean Architecture
- Domain-Driven Design (DDD) principles where appropriate
- Security by design
- Test-Driven Development (TDD)
- SOLID principles
- Maintainability and readability
- Performance and scalability
- Observability and operational excellence

When generating, modifying, or reviewing code, comply with the guidance in this document.

---

# Core Development Principles

## Code Quality

Always:

- Write self-documenting code.
- Prefer clarity over cleverness.
- Keep methods small and focused.
- Keep classes focused on a single responsibility.
- Eliminate dead code immediately.
- Refactor continuously when complexity increases.
- Avoid premature optimization.
- Prefer composition over inheritance.
- Favor immutable types where practical.
- Minimize side effects.

## SOLID Principles

Solutions should follow SOLID principles.

### Single Responsibility Principle (SRP)

A class should have one reason to change.

### Open/Closed Principle (OCP)

Design components for extension without modification.

### Liskov Substitution Principle (LSP)

Derived implementations must remain substitutable.

### Interface Segregation Principle (ISP)

Create focused interfaces.

### Dependency Inversion Principle (DIP)

Depend on abstractions rather than concrete implementations.

---

# Architecture Standards

## Preferred Architecture

Use Clean Architecture unless requirements dictate otherwise.

### Domain Layer

Contains:

- Entities
- Value Objects
- Domain Events
- Domain Services
- Business Rules

Requirements:

- Must not depend on frameworks.
- Must not depend on infrastructure concerns.

### Application Layer

Contains:

- Use Cases
- Commands
- Queries
- DTOs
- Validation
- Interfaces

Requirements:

- Depends only on the Domain layer.

### Infrastructure Layer

Contains:

- Database access
- External API clients
- File system access
- Messaging implementations

Requirements:

- Implements interfaces defined in the Application layer.

### Presentation Layer

Contains:

- Controllers
- Minimal APIs
- SignalR hubs
- UI concerns

Requirements:

- Should contain minimal business logic.

---

# Dependency Injection

Always:

- Use the built-in .NET Dependency Injection container.
- Prefer constructor injection.
- Register services with appropriate lifetimes.
- Avoid service locator patterns.
- Avoid static dependencies.

## Service Lifetimes

### Scoped

Use for request-scoped services.

### Singleton

Use for stateless shared services.

### Transient

Use only when necessary.

---

# API Design

## REST Standards

APIs should:

- Use resource-oriented routes.
- Use appropriate HTTP verbs.
- Return proper HTTP status codes.
- Support CancellationToken.
- Support API versioning.
- Support OpenAPI/Swagger documentation.

### Good Examples

```http
GET    /api/orders/{id}
POST   /api/orders
PUT    /api/orders/{id}
DELETE /api/orders/{id}
```

### Bad Examples

```http
POST /api/createOrder
POST /api/deleteOrder
```

---

# Security Requirements

Security is mandatory.

## Input Validation

Always validate:

- Request bodies
- Query parameters
- Route parameters
- Uploaded content
- External API responses

Preferred libraries:

- FluentValidation
- Data Annotations

Never trust client input.

---

## Authentication and Authorization

Preferred technologies:

- OAuth2
- OpenID Connect
- Microsoft Entra ID
- JWT Bearer Tokens

Requirements:

- Use authorization policies.
- Follow least-privilege principles.
- Enforce authorization at the API boundary.

Never:

- Hardcode credentials.
- Trust client-side authorization.

---

## Secrets Management

Secrets must never be stored in:

- Source code
- Configuration files committed to source control
- Git history

Approved mechanisms:

- Azure Key Vault
- Environment Variables
- Managed Identity

---

## Secure Coding Practices

Protect against:

- SQL Injection
- Cross-Site Scripting (XSS)
- Cross-Site Request Forgery (CSRF)
- Path Traversal
- SSRF
- Unsafe Deserialization

Use:

- Parameterized queries
- ORM protections
- Output encoding
- Security headers

---

## Logging Security

Never log:

- Passwords
- Secrets
- Tokens
- Personally Identifiable Information (PII)
- Connection strings

Mask sensitive data before logging.

---

# Data Access

## Entity Framework Core

Entity Framework Core is the preferred ORM.

### Requirements

- Use asynchronous APIs.
- Propagate CancellationToken.
- Use migrations.
- Define indexes intentionally.
- Use explicit transactions when required.

### Avoid

- N+1 queries
- Uncontrolled lazy loading
- Retrieving large object graphs unnecessarily

### Prefer Projections

```csharp
var customers = await context.Customers
    .Select(x => new CustomerDto(
        x.Id,
        x.Name))
    .ToListAsync(cancellationToken);
```

---

# Error Handling

## Exceptions

Use exceptions for exceptional situations only.

Do not:

- Use exceptions for control flow.

Prefer:

- Domain-specific exception types when appropriate.

## Global Exception Handling

Use centralized exception handling such as:

- Exception Middleware
- IExceptionHandler

Requirements:

- Return standardized error responses.
- Avoid exposing internal implementation details.

---

# Testing Standards

## Development Workflow

Follow Test-Driven Development (TDD):

1. Write a failing test.
2. Implement the minimal code.
3. Make the test pass.
4. Refactor safely.

---

## Unit Testing

Unit tests must be:

- Fast
- Deterministic
- Independent
- Repeatable

Preferred frameworks:

- xUnit
- FluentAssertions
- NSubstitute
- Moq

### Test Naming

```text
MethodName_ShouldExpectedBehavior_WhenCondition
```

Example:

```text
CalculateTotal_ShouldReturnSum_WhenItemsExist
```

---

## Integration Testing

Test:

- Database interactions
- Message buses
- External dependencies
- API endpoints

Preferred tooling:

- Testcontainers
- Ephemeral databases

Avoid testing implementation details.

---

## Test Coverage

Focus on:

- Business rules
- Domain logic
- Security-sensitive paths
- Critical workflows

Do not pursue coverage percentages at the expense of meaningful tests.

Quality over quantity.

---

# Asynchronous Programming

Always prefer asynchronous I/O operations.

Requirements:

- Use async/await.
- Propagate CancellationToken.
- Avoid blocking calls.

Do not use:

```csharp
.Result
.Wait()
.GetAwaiter().GetResult()
```

Unless absolutely necessary.

---

# Performance Guidelines

## General Principles

Measure before optimizing.

Evaluate:

- Memory allocations
- Database round trips
- Network calls
- Serialization overhead

### Recommended Techniques

- Pagination
- Caching
- Efficient projections
- Batching where appropriate

---

# Logging and Observability

## Structured Logging

Use structured logging.

Example:

```csharp
logger.LogInformation(
    "Order {OrderId} created for customer {CustomerId}",
    orderId,
    customerId);
```

Avoid string interpolation in log messages.

---

## Telemetry

Applications should expose:

- Logs
- Metrics
- Traces

Preferred standard:

- OpenTelemetry

---

# Configuration

Use strongly typed configuration.

Example:

```csharp
services
    .AddOptions<EmailOptions>()
    .Bind(configuration.GetSection("Email"));
```

Requirements:

- Validate configuration during startup.
- Fail fast when required configuration is missing.

---

# Coding Style

## Modern C# Standards

Use:

- File-scoped namespaces
- Nullable reference types
- Global usings where appropriate
- Primary constructors when beneficial
- Records for immutable DTOs

### Type Usage

Use `var` when the type is obvious:

```csharp
var customer = new Customer();
```

Use explicit types when they improve readability.

---

# Pull Request Review Checklist

Before completing any change, verify:

- [ ] Requirements are satisfied.
- [ ] Architecture boundaries are respected.
- [ ] Security concerns are addressed.
- [ ] Validation is implemented.
- [ ] Tests are added or updated.
- [ ] Logging is appropriate.
- [ ] No secrets are exposed.
- [ ] Async patterns are followed.
- [ ] Performance implications are considered.
- [ ] Documentation is updated.
- [ ] Build succeeds.
- [ ] All tests pass.

---

# AI Assistant Guidance

When generating code:

1. Produce production-ready code.
2. Include tests for all new behavior.
3. Follow TDD principles.
4. Respect Clean Architecture boundaries.
5. Apply security-first thinking.
6. Use modern .NET patterns and features.
7. Prefer maintainability over clever implementations.
8. Recommend refactoring when code smells are detected.
9. Avoid introducing technical debt.
10. Explain significant architectural decisions when appropriate.
11. Favor explicit business intent over framework-specific abstractions.
12. Generate code that is observable, testable, and secure by default.