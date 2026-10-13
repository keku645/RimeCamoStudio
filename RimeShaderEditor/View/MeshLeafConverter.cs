using System;
using System.Globalization;
using System.Windows.Data;

namespace RimeShaderEditor.View;

/// <summary>
/// The readable half of a mesh path for a list row: "roadblockhedgehog_01_mesh" out of
/// "props/streetprops/roadblockhedgehog_01/roadblockhedgehog_01_mesh".
///
/// ⛔ The row shows this, never the value: what distinguishes two objects lives at the END of the path, and
/// a narrow column trims from the end, so the full path in the row reads as the same folder repeated. The
/// item keeps the whole path as its value and its tooltip — nothing downstream sees the short form.
/// </summary>
public sealed class MeshLeafConverter : IValueConverter
{
    public object Convert(object? p_Value, Type p_TargetType, object? p_Parameter, CultureInfo p_Culture) =>
        p_Value is string s_Path && s_Path.Length > 0 ? s_Path.Split('/')[^1] : p_Value ?? "";

    public object ConvertBack(object? p_Value, Type p_TargetType, object? p_Parameter, CultureInfo p_Culture) =>
        throw new NotSupportedException();
}
