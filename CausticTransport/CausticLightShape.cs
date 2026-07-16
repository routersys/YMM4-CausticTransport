using System.ComponentModel.DataAnnotations;

namespace CausticTransport;

public enum CausticLightShape
{
    [Display(Name = nameof(Texts.ShapePlane), Description = nameof(Texts.ShapePlaneDescription), ResourceType = typeof(Texts))]
    Plane = 0,

    [Display(Name = nameof(Texts.ShapeCircle), Description = nameof(Texts.ShapeCircleDescription), ResourceType = typeof(Texts))]
    Circle = 1,

    [Display(Name = nameof(Texts.ShapeHorizontalSlit), Description = nameof(Texts.ShapeHorizontalSlitDescription), ResourceType = typeof(Texts))]
    HorizontalSlit = 2,

    [Display(Name = nameof(Texts.ShapeVerticalSlit), Description = nameof(Texts.ShapeVerticalSlitDescription), ResourceType = typeof(Texts))]
    VerticalSlit = 3,
}
