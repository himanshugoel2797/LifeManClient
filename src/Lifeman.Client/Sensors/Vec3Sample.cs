namespace Lifeman.Client.Sensors;

/// 3-axis sensor sample. Used for accelerometer, gyroscope, magnetometer.
public readonly record struct Vec3Sample(float X, float Y, float Z)
{
    /// Euclidean magnitude. Used by peak detector and motion classifier.
    public float Magnitude
    {
        get
        {
            // double precision intermediate avoids underflow on tiny vectors.
            var mx = (double)X; var my = (double)Y; var mz = (double)Z;
            return (float)Math.Sqrt(mx * mx + my * my + mz * mz);
        }
    }
}
