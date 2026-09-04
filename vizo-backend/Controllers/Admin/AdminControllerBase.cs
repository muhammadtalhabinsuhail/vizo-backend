using vizo_backend.Models;

namespace vizo_backend.Controllers.Admin;

/// <summary>
/// Base for every controller in /Controllers/Admin.
///
/// It adds nothing of its own -- all the plumbing (Fail, Now, Today,
/// CurrentUserId, Initials, Log) lives in <see cref="ApiControllerBase"/>,
/// which the module controllers outside this folder share. This type exists
/// only so the admin folder has one obvious place to hang anything that turns
/// out to be admin-specific, and so the inheritance in these ten files reads
/// as "an admin controller" rather than "a controller".
///
/// The security boundary is NOT here: it is the
/// [Authorize(Policy = "SuperAdmin")] attribute on each concrete controller.
/// Keeping it on the concrete class means it shows up in the file you are
/// reading instead of being inherited invisibly.
/// </summary>
public abstract class AdminControllerBase : ApiControllerBase
{
    protected AdminControllerBase(AppDbContext db, IConfiguration cfg,
        ILogger logger, IWebHostEnvironment env) : base(db, cfg, logger, env) { }
}
