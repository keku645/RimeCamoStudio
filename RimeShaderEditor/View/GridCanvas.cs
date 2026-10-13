// Pan/zoom/grid mechanics adapted from Frosty Editor's LevelEditorPlugin GridCanvas (MIT licensed).
// Kept close to the original because the zoom-toward-cursor and adaptive grid-subdivision maths are
// already proven; only the parts this editor does not need were dropped.

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace RimeShaderEditor.View;

public class GridCanvas : Control
{
    protected double m_Scale = 1.0;
    protected double m_ScaleLevel = 1.0;
    protected double m_ScaleRate = 0.075;

    /// <summary>User multiplier on the wheel-zoom step (Settings). 1 = historical speed.</summary>
    public static double ZoomSpeed { get; set; } = 1.0;

    protected double m_MinorUnits = 10;
    protected double m_MajorUnits = 100;

    protected TranslateTransform m_Viewport = new(0, 0);
    protected TranslateTransform m_Offset = new(0, 0);
    protected Point m_PrevMousePos;

    protected Pen m_GridMinorPen = new(new SolidColorBrush(Color.FromRgb(58, 58, 60)), 1.0);
    protected Pen m_GridMajorPen = new(new SolidColorBrush(Color.FromRgb(72, 72, 75)), 1.0);
    protected Pen m_AxisPen = new(new SolidColorBrush(Color.FromRgb(90, 90, 94)), 1.0);

    public GridCanvas()
    {
        Focusable = true;
        Background = new SolidColorBrush(Color.FromRgb(37, 37, 38));
        m_GridMinorPen.Freeze();
        m_GridMajorPen.Freeze();
        m_AxisPen.Freeze();
    }

    public Point TransformPoint(Point p_Point) => GetWorldMatrix().Inverse.Transform(p_Point);

    protected override void OnMouseWheel(MouseWheelEventArgs p_Args)
    {
        var s_Matrix = new MatrixTransform(m_Scale, 0, 0, m_Scale, m_Viewport.X, m_Viewport.Y);
        var s_MousePos = s_Matrix.Inverse.Transform(p_Args.GetPosition(this));

        var s_Percent = (m_Scale - m_ScaleLevel * 0.5) / (m_ScaleLevel * 2 - m_ScaleLevel * 0.5);
        var s_Rate = ((1 - s_Percent) * (m_ScaleRate * 0.5) + s_Percent * (m_ScaleRate * 2)) * ZoomSpeed;

        if (p_Args.Delta < 0)
        {
            if (m_Scale < 0.15)
                return;

            m_Scale -= s_Rate;
        }
        else
        {
            if (m_Scale > 2.0)
                return;

            m_Scale += s_Rate;
        }

        UpdateScaleParameters();

        s_Matrix = new MatrixTransform(m_Scale, 0, 0, m_Scale, m_Viewport.X, m_Viewport.Y);
        var s_NewPos = s_Matrix.Inverse.Transform(p_Args.GetPosition(this));

        m_Offset.X -= s_NewPos.X - s_MousePos.X;
        m_Offset.Y -= s_NewPos.Y - s_MousePos.Y;

        InvalidateVisual();
    }

    protected override void OnMouseDown(MouseButtonEventArgs p_Args)
    {
        if (p_Args.Handled)
            return;

        Focus();
        if (p_Args.ChangedButton != MouseButton.Right)
            return;

        var s_Matrix = new MatrixTransform(m_Scale, 0, 0, m_Scale, m_Viewport.X, m_Viewport.Y);
        m_PrevMousePos = s_Matrix.Inverse.Transform(p_Args.GetPosition(this));

        Mouse.Capture(this);
        p_Args.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs p_Args)
    {
        if (p_Args.Handled || p_Args.RightButton != MouseButtonState.Pressed)
            return;

        var s_Matrix = new MatrixTransform(m_Scale, 0, 0, m_Scale, m_Viewport.X, m_Viewport.Y);
        var s_MousePos = s_Matrix.Inverse.Transform(p_Args.GetPosition(this));

        m_Offset.X += m_PrevMousePos.X - s_MousePos.X;
        m_Offset.Y += m_PrevMousePos.Y - s_MousePos.Y;

        m_PrevMousePos = s_MousePos;
        p_Args.Handled = true;
        InvalidateVisual();
    }

    protected override void OnMouseUp(MouseButtonEventArgs p_Args)
    {
        if (p_Args.Handled || p_Args.ChangedButton != MouseButton.Right)
            return;

        Mouse.Capture(null);
        p_Args.Handled = true;
    }

    protected override Size MeasureOverride(Size p_Constraint)
    {
        if (!double.IsInfinity(p_Constraint.Width) && !double.IsInfinity(p_Constraint.Height))
            m_Viewport = new TranslateTransform(p_Constraint.Width * 0.5, p_Constraint.Height * 0.5);

        return base.MeasureOverride(p_Constraint);
    }

    protected sealed override void OnRender(DrawingContext p_Context)
    {
        base.OnRender(p_Context);

        p_Context.PushClip(new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight)));
        p_Context.DrawRectangle(Background, null, new Rect(0, 0, ActualWidth, ActualHeight));

        DrawGrid(p_Context);
        Render(p_Context);

        p_Context.Pop();
    }

    protected virtual void Render(DrawingContext p_Context)
    {
    }

    private void DrawGrid(DrawingContext p_Context)
    {
        var s_View = GetViewMatrix();
        var s_InvScale = 1 / m_Scale;

        DrawGridLines(p_Context, s_View, s_InvScale, m_MinorUnits, m_GridMinorPen);
        DrawGridLines(p_Context, s_View, s_InvScale, m_MajorUnits, m_GridMajorPen);

        p_Context.DrawLine(m_AxisPen,
            new Point(-m_Offset.X * m_Scale + ActualWidth * 0.5, 0),
            new Point(-m_Offset.X * m_Scale + ActualWidth * 0.5, ActualHeight));
        p_Context.DrawLine(m_AxisPen,
            new Point(0, -m_Offset.Y * m_Scale + ActualHeight * 0.5),
            new Point(ActualWidth, -m_Offset.Y * m_Scale + ActualHeight * 0.5));
    }

    private void DrawGridLines(DrawingContext p_Context, MatrixTransform p_View, double p_InvScale, double p_Units, Pen p_Pen)
    {
        var s_StartX = m_Viewport.X * p_InvScale - m_Viewport.X * p_InvScale % p_Units + m_Offset.X % p_Units;
        var s_StartY = m_Viewport.Y * p_InvScale - m_Viewport.Y * p_InvScale % p_Units + m_Offset.Y % p_Units;

        var s_CountX = (int) (ActualWidth * p_InvScale / p_Units) + 2;
        var s_CountY = (int) (ActualHeight * p_InvScale / p_Units) + 2;

        for (var i = -1; i < s_CountX; i++)
        {
            var s_X = -s_StartX + i * p_Units;
            p_Context.DrawLine(p_Pen,
                p_View.Transform(new Point(s_X, -ActualHeight * p_InvScale)),
                p_View.Transform(new Point(s_X, ActualHeight * p_InvScale)));
        }

        for (var i = -1; i < s_CountY; i++)
        {
            var s_Y = -s_StartY + i * p_Units;
            p_Context.DrawLine(p_Pen,
                p_View.Transform(new Point(-ActualWidth * p_InvScale, s_Y)),
                p_View.Transform(new Point(ActualWidth * p_InvScale, s_Y)));
        }
    }

    protected MatrixTransform GetWorldMatrix()
    {
        var s_Offset = new Matrix(1, 0, 0, 1, -m_Offset.X, -m_Offset.Y);
        var s_Scale = new Matrix(m_Scale, 0, 0, m_Scale, 0, 0);
        var s_Viewport = new Matrix(1, 0, 0, 1, m_Viewport.X, m_Viewport.Y);

        return new MatrixTransform(s_Offset * s_Scale * s_Viewport);
    }

    protected MatrixTransform GetViewMatrix()
    {
        var s_Scale = new Matrix(m_Scale, 0, 0, m_Scale, 0, 0);
        var s_Viewport = new Matrix(1, 0, 0, 1, m_Viewport.X, m_Viewport.Y);

        return new MatrixTransform(s_Scale * s_Viewport);
    }

    private void UpdateScaleParameters()
    {
        if (m_Scale > m_ScaleLevel * 2)
        {
            m_ScaleRate *= 2;
            m_ScaleLevel *= 2;
            m_MinorUnits /= 2;
            m_MajorUnits /= 2;
        }
        else if (m_Scale < m_ScaleLevel)
        {
            m_ScaleRate /= 2;
            m_ScaleLevel *= 0.5;
            m_MinorUnits *= 2;
            m_MajorUnits *= 2;
        }

        if (m_MinorUnits * m_Scale <= 5.0)
        {
            m_MinorUnits *= 2;
            m_MajorUnits *= 2;
        }
    }
}
