using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using vizo_backend.Models;

namespace vizo_backend.Controllers;

/// <summary>
/// The company's NAME, for the places the app shows it -- the sidebar beside
/// the logo, the sign-in page's footer.
///
/// Those places said "AdvPOS", typed into the front end. AdvPOS is the name of
/// the SOFTWARE; the business using it is whatever "Company"."CompanyName"
/// says (VIZO Pakistan today), and a Super Admin can change that at
/// /admin/settings. GET /admin/company already returns it, but only to the
/// Super Admin, and with the NTN, phone and address besides -- so this is its
/// own small endpoint.
///
/// Anonymous on purpose: the sign-in page shows it before anybody has signed
/// in. It returns nothing that is not already printed on every invoice. The
/// browser caches the answer (lib/company.ts), so this is asked at most once
/// per page load and the first paint never waits for it.
/// </summary>
[Route("api/company")]
[ApiController]
[AllowAnonymous]
public class CompanyBrandController : ApiControllerBase
{
    public CompanyBrandController(AppDbContext db, IConfiguration cfg,
        ILogger<CompanyBrandController> logger, IWebHostEnvironment env)
        : base(db, cfg, logger, env) { }

    [HttpGet("brand")]
    public async Task<IActionResult> Brand()
    {
        try
        {
            var c = await _db.Companies.AsNoTracking()
                .OrderBy(x => x.CompanyId)
                .Select(x => new { name = x.CompanyName, legalName = x.LegalName })
                .FirstOrDefaultAsync();

            /* Five minutes in any cache between here and the browser: a rename
               at /admin/settings shows within that, and nobody asks twice. */
            Response.Headers.CacheControl = "public, max-age=300";
            return Ok(c ?? new { name = "", legalName = "" });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load the company name");
        }
    }
}
