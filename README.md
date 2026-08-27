# Caseware Collaborate Authentication & Authorization Assessment

**Created by:** Nicolás Salinas  
**GitHub:** [jnsalinas](https://github.com/jnsalinas)  
**Email:** [jnsalinasgo@gmail.com](mailto:jnsalinasgo@gmail.com)  
**LinkedIn:** [linkedin.com/in/jnsalinasgo](https://www.linkedin.com/in/jnsalinasgo)

Take-home assessment: design for Collaborate login and permissions, plus a small API that checks JWTs.

Yellow boxes in the diagram are what I am proposing. Identity providers and resource APIs already exist.

**Assumptions:** I am not building password storage or MFA. Caseware already has a central login, and some firms can use their own SAML/OIDC login. The permissions database can send events when a role or permission changes. Resource APIs check the token themselves and do not query that database. Redis is available (ElastiCache on AWS).

# Part 1: Architecture & Design (Primary Focus)

## 1. Architecture Components

### High-Level Architecture Diagram

Who calls the system, the layer I am proposing, existing logins, permission storage, and the resource APIs.

```mermaid
flowchart TB
    subgraph Callers["Callers"]
        direction LR
        Staff[Firm Staff<br/>Human User]
        External[Invited External Client User<br/>Human User]
        System[Client System / Integration<br/>Machine Client]
    end

    subgraph Designed["Proposed Collaborate Identity & Authorization Layer"]
        direction LR
        Identity[Identity Federation & Token Service<br/>OAuth 2.0 / OIDC]
        Decision[Authorization Decision Service<br/>Fine-grained permission checks]
        Redis[Redis Authorization Cache<br/>Fast decisions and revocation]

        Identity --> Decision
        Decision <--> Redis
    end

    Token[/Collaborate Access Token/]

    APIs[Resource APIs<br/>Document Service / Comments Service / Financial Data API]

    DB[(Collaborate Permissions Database<br/>Workspace roles: owner / contributor / viewer<br/>Resource overrides and firm policy)]

    subgraph Providers["Identity Providers - Existing External Dependencies"]
        direction LR
        CasewareIdP[Caseware Central IdP<br/>OIDC]
        ExternalIdP[External Firm IdP - optional<br/>SAML / OIDC]
    end

    Staff -->|Start interactive login| Identity
    External -->|Start interactive login| Identity
    System -->|OAuth client authentication<br/>or delegated access| Identity

    Identity -->|Issue| Token

    Staff -->|Request resource + access token| APIs
    External -->|Request resource + access token| APIs
    System -->|Request resource + access token| APIs

    APIs -->|Validate token locally| APIs
    APIs -->|Request fine-grained<br/>authorization decision| Decision

    Redis -. Cache miss .-> DB
    DB -->|Permission or role change event| Decision

    Identity <-->|Login redirect + callback| CasewareIdP
    Identity <-->|Login redirect + callback<br/>when federation is configured| ExternalIdP

    classDef designed fill:#FFE28A,stroke:#A66E00,stroke-width:3px,color:#222;
    classDef external fill:#E5E7EB,stroke:#6B7280,color:#222;
    classDef data fill:#DDEBFF,stroke:#2563EB,color:#222;
    classDef api fill:#DDF5E7,stroke:#15803D,color:#222;
    classDef token fill:#F5E8FF,stroke:#9333EA,color:#222;

    class Identity,Decision,Redis designed;
    class Staff,External,System,CasewareIdP,ExternalIdP external;
    class DB data;
    class APIs api;
    class Token token;
```

1. **Callers**

   - **Firm Staff**: people who work at a Caseware firm.
   - **Invited External Client Users**: people from outside the firm invited to a workspace.
   - **Client System / Integration**: a company's system calling Collaborate APIs, with no human in that call.

2. **Collaborate Identity & Authorization Layer**

   Handles login, tokens, permission checks, and taking access away. It exposes OAuth 2.0 / OIDC endpoints (authorize, token, discovery) and calls the Caseware IdP for discovery, token, and userinfo — we do not build those.

   It finds the firm and picks the right Identity Provider. Each firm has its own client settings (redirect URLs, IdP details).

   - Firm staff log in with the **Caseware Central IdP**.
   - External users can log in with an **External Firm IdP** when that firm has it set up.
   - Client systems log in as an OAuth client, or they act on behalf of a user.

   After login, this layer maps the identity to a Collaborate user and firm, and creates a short-lived Collaborate access token.

3. **Identity Providers**

   They prove who the user is. They already exist — I am not building them.

   - **Caseware Central IdP**: OIDC, for firm staff.
   - **External Firm IdP**: SAML or OIDC, for firms that bring their own login.

   Collaborate sends the user to the IdP and gets them back on a callback URL.

4. **Redis Authorization Cache**

   Stores recent permission answers so we do not hit the database on every request. That is how we keep up with tens of thousands of checks per second.

   When a user is removed from a workspace, we clear their cached permissions so access can stop within seconds, even if the token is still valid.

5. **Collaborate Permissions Database**

   Source of truth for permissions:

   - Workspace roles: `owner`, `contributor`, `viewer`.
   - Extra rules for a single document (for example, share one file with one external user).
   - Firm-level rules.

   If Redis does not have the answer, we read this database and then store the result in Redis.

6. **Collaborate Access Token**

   After login, the caller gets a short-lived Collaborate token with the user, firm, scopes, audience, and expiry.

   We do not pass the IdP token to resource APIs. The IdP does not know about Collaborate workspaces, so we create our own token.

7. **Resource Request and Resource APIs**

   The caller uses that token to ask for a document, comment, or financial data.

   Each API checks the token locally, then asks the Authorization Decision Service if this user can access this resource. If no, it returns `403 Forbidden`.

**Login.** Authorization Code + PKCE. Staff use Caseware login. Invited users use their firm's login when it is set up.

**Long-lived sessions.** If someone is already editing a document, clearing Redis is not enough. The same event should tell the API to check again or close that connection. We do not force everyone to log in again.

**On-behalf-of.** Two cases: a client's system calling Collaborate for one of their employees, and an internal service calling another Caseware API after a user did something. We give them a new, smaller token for that user and that API. It cannot have more access than the user (confused deputy). We still log who acted and for whom.

## 2. Implementation Plan

1. **Requirements and Scope** — What we will build, what we will not, and the security limits.
2. **Solution Design** — Architecture, APIs, firm login routing, token claims, permission model, Redis.
3. **Prioritization** — Login, token checks, and resource access first, in small steps.
4. **Team and Responsibilities** — Backend, identity/security, platform, and QA.
5. **Project Setup and Development** — ASP.NET Core project, auth, APIs, Redis, and the database.
6. **Testing** — Unit, integration, security, performance, and end-to-end tests.
7. **Release** — Secrets, monitoring, audit logs, alerts, deploy pipelines, slow rollout.

## 3. Testing Strategy

Prove people can log in, only see what they should, and lose access quickly when permissions change.

1. **Unit Tests** — Roles, scopes, one-document exceptions, firm rules.
2. **Authentication Tests** — Valid, expired, and invalid tokens. Bad token → `401`. Valid token without permission → `403`.
3. **Integration Tests** — Login callbacks, PKCE, token creation, protected endpoints.
4. **Cache and Revocation Tests** — Redis hit/miss, database fallback. After we remove a user, they lose access within seconds — including if they still have a document open.
5. **Delegated Access Tests** — On-behalf-of tokens expire soon and cannot have more access than the original user.
6. **Performance Tests** — Permission checks stay fast under many requests.

## 4. Evaluation & Observability

- Count successful/failed logins, `401` / `403`, and bad or expired tokens.
- Measure how long a permission check takes, and how often Redis has the answer.
- Measure how long it takes to deny access after a permission change, including closing live sessions.
- Audit logs: who did what, for whom, on which resource, and when permissions changed.
- Alert if logins fail a lot, Redis is down, or permission events are late.

## 5. Failure Modes & Tradeoffs

- **Redis** is faster, but it can still say "yes" for a short time after we remove someone. Events, short-lived tokens, and closing live sessions shrink that window.
- **Short-lived tokens** are safer if stolen, but clients refresh more often.
- **A firm's own login** is nicer for users, but more setup per firm.
- **One Decision Service** keeps rules the same across APIs, but it is another network call. Redis and local JWT checks help.
- If Redis is down, ask the database with a short timeout, or deny sensitive access.
- If a permission event is late, access may stay valid briefly. Monitoring and short-lived tokens help.
- If an IdP is down, new logins fail. People with a valid Collaborate token keep working until it expires.
- If a service token is reused as a user token, a service could do more than the user is allowed. Token swap must stay limited to that user and that API.

# Part 2: Targeted Implementation

## A. API Prototype — JWT Authentication

A small ASP.NET Core API that only serves the request when the JWT has the right scope.

**Library:** `Microsoft.AspNetCore.Authentication.JwtBearer`

**Why ASP.NET Core / .NET:** I used ASP.NET Core because JWT auth and policies are already built in. I did not write my own JWT parser. A full identity server would be too much for this slice.

I implemented **A** only (not B or C). That is the first thing Document, Comments, and Financial Data APIs need.

The `DocumentRead` policy requires `scope = documents.read`. No token or a bad token → `401`. A good token without the scope → `403`.

This slice does not call the Decision Service. A real Document API would check the JWT first, then ask the Decision Service.

`dotnet user-jwts` stands in for the Collaborate token service. There is no real identity provider.

| Method | Endpoint | Auth | Description |
| --- | --- | --- | --- |
| `GET` | `/api/documents` | JWT Bearer + `documents.read` | Success when the token is valid and has the right scope |

- `200 OK` — valid JWT with `documents.read`
- `401 Unauthorized` — missing, invalid, or expired token
- `403 Forbidden` — valid JWT without the required scope

**Run:** `dotnet run --project Collaborate.Authorization.Api`, open `/swagger`, paste a JWT from `dotnet user-jwts`.

### Evidence

**1. Valid JWT — 200 OK**

![Valid JWT — 200 OK](docs/evidence/01-valid-jwt-200.png)

**2. Missing / invalid JWT — 401 Unauthorized**

![Missing or invalid JWT — 401 Unauthorized](docs/evidence/02-invalid-jwt-401.png)

**3. Valid JWT without scope — 403 Forbidden**

![Valid JWT without scope — 403 Forbidden](docs/evidence/03-missing-scope-403.png)

# AI usage

- AI helped with the README layout, the Mermaid diagram, and the JwtBearer/Swagger setup.
- I kept the design choices (PKCE, Redis, closing live sessions, token swap, slice A only). I did not turn Part 2 into a full login system.
- I would let people use AI for wiring and docs, then check `401` vs `403` myself, and that a swapped token never has more access than the user.
