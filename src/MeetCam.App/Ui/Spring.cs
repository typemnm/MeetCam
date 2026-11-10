namespace MeetCam;

internal sealed class Spring
{
    public float Value = 1f;
    public float Target = 1f;
    public float Velocity;
    public float Stiffness = 280f;
    public float Damping = 12f;

    public void Tick(float dt)
    {
        dt = Math.Clamp(dt, 0.001f, 0.034f);
        var acceleration = ((Target - Value) * Stiffness) - (Velocity * Damping);
        Velocity += acceleration * dt;
        Value += Velocity * dt;
        if (Resting)
        {
            Value = Target;
            Velocity = 0;
        }
    }

    public bool Resting => MathF.Abs(Target - Value) < 0.0035f && MathF.Abs(Velocity) < 0.03f;

    public void Kick(float value, float velocity, float target)
    {
        Value = value;
        Velocity = velocity;
        Target = target;
    }
}
