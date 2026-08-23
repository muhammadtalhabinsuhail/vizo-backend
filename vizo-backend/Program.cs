//using Microsoft.AspNetCore.Authentication.JwtBearer;
//using Microsoft.EntityFrameworkCore;
//using Microsoft.IdentityModel.Tokens;
//using System.Text;
//using vizo_backend.Models;

//var builder = WebApplication.CreateBuilder(args);

//// Add services to the container.

//builder.Services.AddControllers();
//// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle



//builder.Services.AddDbContext<AppDbContext>(options =>
//    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));


//builder.Services.AddEndpointsApiExplorer();
//builder.Services.AddSwaggerGen();


//builder.Services.AddCors(options =>
//{
//    options.AddPolicy("AllowAll", policy =>
//    {
//        policy.WithOrigins(
//                "http://localhost:3000",
//                "https://www.vizo.com.pk/"
//            )
//            .AllowAnyHeader()
//            .AllowAnyMethod()
//            .AllowCredentials();
//    });
//});



//var jwtKey = builder.Configuration["Jwt:Key"]!;
//builder.Services.AddAuthentication(options =>
//{
//    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
//    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
//})
//    .AddJwtBearer(options =>
//    {
//        options.TokenValidationParameters = new TokenValidationParameters
//        {
//            ValidateIssuer = true,
//            ValidateAudience = true,
//            ValidateLifetime = true,
//            ValidateIssuerSigningKey = true,
//            ValidIssuer = builder.Configuration["Jwt:Issuer"],
//            ValidAudience = builder.Configuration["Jwt:Audience"],
//            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey))
//        };
//    });


//var app = builder.Build();

//// Configure the HTTP request pipeline.
//if (app.Environment.IsDevelopment())
//{
//    app.UseSwagger();
//    app.UseSwaggerUI();
//}

//app.UseHttpsRedirection();
//app.UseCors("AllowAll");


//app.UseAuthentication();
//app.UseAuthorization();


//app.MapControllers();

//app.MapGet("/", () => "☑️ API is working");

//app.Run();




using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using System.Security.Claims;
using System.Text;
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