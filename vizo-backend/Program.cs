// ═══════════════════════════════════════════════════════════════════════════
//  CONFIGURATION -- the two files this project needs that are NOT in git
// ═══════════════════════════════════════════════════════════════════════════
//
//  Both files below are git-ignored because they carry live secrets: the Neon
//  connection string, the JWT signing key, two Cloudinary API secrets and a
//  Gmail app password. They are reproduced here in FULL STRUCTURE so whoever
//  clones this repository knows exactly what to create and where.
//
//  THE SECRET VALUES ARE REDACTED TO <PLACEHOLDERS>. This repository is PUBLIC.
//  Committing the real ones here would publish a live database password, a JWT
//  signing key and a Gmail app password to anybody who opens the page. Copy the
//  real values from the running machine, or better, from user-secrets.
//
//  NOTE: vizo-backend/appsettings.json is ALREADY COMMITTED to this repository
//  with the real values in it, and they are in the git history. Redacting them
//  here does not undo that. They need rotating -- see the README.
//
//  ROTATE THESE BEFORE THIS GOES ANYWHERE REAL, and move them to user-secrets:
//      dotnet user-secrets set "ConnectionStrings:DefaultConnection" "..."
//      dotnet user-secrets set "Jwt:Key" "..."
//
//  ---------------------------------------------------------------------------
//  1. backend/vizo-backend/appsettings.json
//  ---------------------------------------------------------------------------
//
//   {
//     "Logging": {
//       "LogLevel": {
//         "Default": "Information",
//         "Microsoft.AspNetCore": "Warning"
//       }
//     },
//     "AllowedHosts": "*",
//
//     "ConnectionStrings": {
//       "DefaultConnection": "Host=ep-rough-glitter-azbo8tb6.c-3.ap-southeast-1.aws.neon.tech;Database=neondb;Username=neondb_owner;Password=<NEON_PASSWORD>;SSL Mode=Require;Trust Server Certificate=true"
//     },
//
//     "Jwt": {
//       "Key": "<JWT_SIGNING_KEY -- 32+ random chars>",
//       "Issuer": "AdvPOS.Api",
//       "Audience": "AdvPOS.Web",
//       "ExpiryMinutes": 480
//     },
//
//     "Cors": {
//       "AllowedOrigins": [ "http://localhost:3000", "https://localhost:3000", "https://www.vizo.com.pk" ]
//     },
//
//     "PasswordReset": {
//       "CodeExpiryMinutes": 30,
//       "MaxAttempts": 5
//     },
//
//     "CloudinaryImages": {
//       "CloudName": "dzzuoem1w",
//       "ApiKey": "266539435255924",
//       "ApiSecret": "<CLOUDINARY_IMAGES_SECRET>",
//       "Folder": "advpos/images"
//     },
//
//     "CloudinaryPdfs": {
//       "CloudName": "dve3ucdo",
//       "ApiKey": "637964151696244",
//       "ApiSecret": "<CLOUDINARY_PDFS_SECRET>",
//       "Folder": "advpos/documents"
//     },
//
//     "EmailSettings": {
//       "SmtpHost": "smtp.gmail.com",
//       "SmtpPort": 587,
//       "SenderEmail": "muhammadtalhabinsuhail@gmail.com",
//       "SenderPassword": "<GMAIL_APP_PASSWORD>",
//       "SenderName": "AdvPOS",
//       "AdminAlertEmail": "muhammadtalhabinsuhail@gmail.com"
//     }
//   }
//
//  ---------------------------------------------------------------------------
//  2. vizo-erp/.env.local        (frontend)
//  ---------------------------------------------------------------------------
//
//   # ─────────────────────────────────────────────────────────────────────────────
//   # AdvPOS frontend environment
//   #
//   # NEXT_PUBLIC_ is required: these values are read in the browser by the axios
//   # calls that live inside the page components. Nothing secret belongs here --
//   # anything with this prefix ships in the client bundle.
//   #
//   # Change API_BASE_URL for each environment and nothing else has to move.
//   #
//   # HTTPS on 7177 is the "https" profile in
//   # backend/vizo-backend/Properties/launchSettings.json. The browser must trust
//   # the ASP.NET dev certificate or every request fails as a network error with
//   # nothing in the console: run `dotnet dev-certs https --trust` once.
//   # ─────────────────────────────────────────────────────────────────────────────
//
//   NEXT_PUBLIC_API_BASE_URL=https://localhost:7177/api
//
//   # Names of the cookies the login page writes and src/proxy.ts reads.
//   # Keep them in step with src/proxy.ts if you rename them.
//   NEXT_PUBLIC_TOKEN_COOKIE=advpos_token
//   NEXT_PUBLIC_ROLE_COOKIE=advpos_role
//
//  ---------------------------------------------------------------------------
//  How the two line up
//  ---------------------------------------------------------------------------
//
//  The API listens on https://localhost:7177 (the "https" profile in
//  Properties/launchSettings.json) and the frontend points at
//  https://localhost:7177/api. Change the port in ONE place and it must change
//  in the other, or every request fails as a bare network error with nothing in
//  the browser console to explain it.
//
//  The browser must also trust the ASP.NET development certificate, or the same
//  silent failure happens on a fresh machine:
//
//      dotnet dev-certs https --trust
//
//  Cors:AllowedOrigins must contain the frontend's origin (http://localhost:3000).
//  CORS is applied before authentication in the pipeline below -- get that order
//  wrong and the browser's pre-flight OPTIONS gets a 401 before any CORS header
//  is written, which looks exactly like a CORS bug and is not one.
//
// ═══════════════════════════════════════════════════════════════════════════

using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using vizo_backend.Models;

var builder = WebApplication.CreateBuilder(args);

/* ─────────────────────────── Database ─────────────────────────── */
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

/* ─────────────────────────── Controllers ───────────────────────── */
builder.Services.AddControllers()
    .AddJsonOptions(o =>
    {
        // The graph is heavily self-referencing (Account -> parent -> children).
        // Ignore cycles rather than throwing when a controller returns an entity.
        o.JsonSerializerOptions.ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles;
    });

/* ─────────────────────────── JWT bearer ────────────────────────── */
var jwt = builder.Configuration.GetSection("Jwt");
var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt["Key"]!));

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
        options.SaveToken = true;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt["Issuer"],
            ValidateAudience = true,
            ValidAudience = jwt["Audience"],
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = signingKey,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            RoleClaimType = ClaimTypes.Role,
            NameClaimType = ClaimTypes.NameIdentifier
        };
    });

/* ───────────────────────── Authorization ───────────────────────── */
/* Role claim values are the role_key column from the "Role" table, so the
   backend, the JWT and the Next.js proxy (src/proxy.ts) all speak one
   vocabulary: super-admin | accountant | order-dept | sales               */
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("SuperAdmin", p => p.RequireRole("super-admin"));
    options.AddPolicy("Accountant", p => p.RequireRole("accountant", "super-admin"));
    options.AddPolicy("OrderDept", p => p.RequireRole("order-dept", "super-admin"));
    options.AddPolicy("Sales", p => p.RequireRole("sales", "super-admin"));
    options.AddPolicy("Staff", p => p.RequireRole("super-admin", "accountant", "order-dept", "sales"));

    /* Purchases, Inventory, Delivery, Claims and the supplier side of Parties:
       everyone except a sales rep. Mirrors ROUTE_RULES in the front end's
       src/proxy.ts so a screen the proxy allows is a screen the API allows. */
    options.AddPolicy("BackOffice", p => p.RequireRole("super-admin", "accountant", "order-dept"));
});

/* ─────────────────────────── CORS ──────────────────────────────── */
var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
              ?? new[] { "http://localhost:3000" };

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
    {
        policy.WithOrigins(origins)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

/* ─────────────────────────── Swagger ───────────────────────────── */
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "AdvPOS API", Version = "v1" });
    c.CustomSchemaIds(type => type.FullName);
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Paste the token from POST /api/auth/login. No \"Bearer \" prefix needed."
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
            },
            Array.Empty<string>()
        }
    });
});

var app = builder.Build();

/* ───────────────────── Global exception handler ─────────────────────
   Every controller action already sits inside its own try/catch and reports
   through Fail(). This is the safety net for everything that happens OUTSIDE
   an action body and therefore cannot be caught there: model binding, action
   filters, authorization handlers, JSON serialisation of the response.

   Without it those failures return a bare 500 with an empty body -- which is
   exactly the "no error was shown" problem, just moved one layer out.

   It must be registered FIRST so it wraps everything after it.            */
app.Use(async (ctx, next) =>
{
    try
    {
        await next();
    }
    catch (Exception ex)
    {
        var log = ctx.RequestServices.GetRequiredService<ILoggerFactory>()
                     .CreateLogger("UnhandledException");
        log.LogError(ex, "Unhandled {Method} {Path}", ctx.Request.Method, ctx.Request.Path);

        if (ctx.Response.HasStarted) throw;   // too late to rewrite the response

        ctx.Response.Clear();
        ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
        ctx.Response.ContentType = "application/json";

        var dev = ctx.RequestServices.GetRequiredService<IWebHostEnvironment>().IsDevelopment();
        await ctx.Response.WriteAsJsonAsync(new
        {
            message = "The request failed before it reached the controller.",
            error = ex.GetBaseException().Message,
            type = ex.GetBaseException().GetType().Name,
            detail = dev ? ex.ToString() : null
        });
    }
});

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
else
{
    app.UseHttpsRedirection();
}

/* CORS must sit ahead of auth so pre-flight requests are answered. */
app.UseCors("AllowFrontend");

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.MapGet("/", () => "AdvPOS API is running. Swagger: /swagger");

app.Run();
