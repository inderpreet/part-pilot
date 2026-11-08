using System.ComponentModel.DataAnnotations;
using InventoryTracker.Auth;
using InventoryTracker.Data.Entities;
using InventoryTracker.Hosting;
using InventoryTracker.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace InventoryTracker.Pages.Projects;

/// <summary>Creates a project (no id) or edits one. Page handlers only bind, call the service and render.</summary>
[Authorize(Policy = Policies.CanEdit)]
public class EditModel(IProjectService projects, ICurrentUser currentUser) : PageModel
{
    [BindProperty]
    public ProjectForm Input { get; set; } = new();

    public ProjectDetail? Existing { get; private set; }

    public bool IsEdit => Existing is not null;

    /// <summary>Only used to word a hint; the Change number handler enforces <c>RequireAdmin</c> itself.</summary>
    public bool IsAdmin => currentUser.IsAdmin;

    /// <summary>True when the save failed because someone else changed the project first; the page offers a reload link.</summary>
    public bool Conflict { get; private set; }

    public async Task<IActionResult> OnGetAsync(int? id)
    {
        if (id is not null)
        {
            Existing = await projects.GetAsync(id.Value, HttpContext.RequestAborted);
            if (Existing is null)
            {
                return NotFound();
            }

            Input = ProjectForm.From(Existing);
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int? id)
    {
        if (id is not null)
        {
            Existing = await projects.GetAsync(id.Value, HttpContext.RequestAborted);
            if (Existing is null)
            {
                return NotFound();
            }
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        // On edit the number is never taken from the form: only "Change number" (Admin) changes it.
        var input = new ProjectInput(Input.Name, Existing is null ? Input.Number : null, Input.Description, Input.TargetUnits ?? 0, Input.Notes);
        var result = Existing is null
            ? await projects.CreateAsync(input, new ProjectActor(currentUser.Id!, currentUser.IsAdmin), HttpContext.RequestAborted)
            : await projects.UpdateAsync(Existing.Id, Input.RowVersion, input, HttpContext.RequestAborted);

        if (!result.Succeeded)
        {
            Conflict = result.Errors.Any(e => e.Code == "project.conflict");
            ModelState.AddErrors(result, nameof(Input));
            return Page();
        }

        this.Flash(Existing is null ? $"BOM '{result.Value.Name}' created as {result.Value.Number}." : $"BOM '{result.Value.Name}' saved.");
        return RedirectToPage("Detail", new { id = result.Value.Id });
    }

    public sealed class ProjectForm
    {
        [Required(ErrorMessage = "Name is required."), StringLength(Project.NameMaxLength, ErrorMessage = "Name can be at most 100 characters.")]
        public string? Name { get; set; }

        /// <summary>Only used when creating; blank generates the next number.</summary>
        [StringLength(Project.NumberMaxLength, ErrorMessage = "BOM number can be at most 30 characters.")]
        public string? Number { get; set; }

        [Display(Name = "Target units"), Required(ErrorMessage = "Enter how many units the BOM builds.")]
        public int? TargetUnits { get; set; } = 1;

        [StringLength(Project.DescriptionMaxLength, ErrorMessage = "Description can be at most 500 characters.")]
        public string? Description { get; set; }

        [StringLength(Project.NotesMaxLength, ErrorMessage = "Notes can be at most 2000 characters.")]
        public string? Notes { get; set; }

        /// <summary>The version the form was loaded with; a stale one is reported as an edit conflict.</summary>
        public long RowVersion { get; set; }

        public static ProjectForm From(ProjectDetail p) => new()
        {
            Name = p.Name,
            Number = p.Number,
            TargetUnits = p.TargetUnits,
            Description = p.Description,
            Notes = p.Notes,
            RowVersion = p.RowVersion,
        };
    }
}
