using Newtonsoft.Json;
using MeshWeaver.Scopes;
using MeshWeaver.Arithmetics.Aggregation;

namespace MeshWeaver.TestDomain.Scopes.NBody
{
    public record Vector(double X, double Y)
    {
        [JsonIgnore]
        public double Length => Math.Sqrt(LengthSquared);
        [JsonIgnore]
        public double LengthSquared => X * X + Y * Y;

        /**
         * The section below is not needed in FF ==> is injected with arithmetics
         */

        public static Vector operator + (Vector v, Vector v2)  => new(v.X + v2.X, v.Y + v2.Y);
        public static Vector operator + (Vector v, double d)   => new(v.X + d, v.Y + d);
        public static Vector operator + (double d, Vector v)   => new(d + v.X, d + v.Y);
        public static Vector operator - (Vector v1, Vector v2) => new(v1.X - v2.X, v1.Y - v2.Y);
        public static Vector operator - (Vector v, double d)   => new(v.X - d, v.Y - d);
        public static Vector operator * (double d, Vector v)   => new(d * v.X, d * v.Y);
        public static Vector operator * (Vector v, double d)   => new(v.X * d, v.Y * d);
        public static Vector operator / (Vector v, double d)   => new(v.X / d, v.Y / d);
        /*
         * Until here...
         */
    }


    // scopes
    public record BodyIdentity(int Id, int TimeIndex);

    public interface IBody : IScope<BodyIdentity, NBodyStorage>
    {
        static ApplicabilityBuilder Applicability(ApplicabilityBuilder builder) =>
            builder.ForScope<IBody>(s => 
                                        s.WithApplicability<IBodyIntegration>(b => b.Identity.TimeIndex > 0, 
                                                                              o => o.ForMember(b => b.Position).ForMember(b => b.Velocity)));
        Vector Position => GetStorage().GetPosition(Identity);
        double Mass => GetStorage().GetMass(Identity);
        Vector Velocity => GetStorage().GetVelocity(Identity);

        IBody Previous => GetScope<IBody>(Identity with { TimeIndex = Identity.TimeIndex - 1 });
    }

    public interface IBodyIntegration : IBody
    {
        Vector IBody.Position => Previous.Position + DeltaX;
        Vector IBody.Velocity => Previous.Velocity + DeltaV;
        Vector DeltaX
        {
            get
            {
                var dt = GetStorage().DeltaT;
                return dt * Previous.Velocity + (dt / 2) * DeltaV;
            }
        }

        Vector DeltaV => (GetStorage().DeltaT / Mass) * Force.Force ;
        IForce Force => GetScope<IForce>();
    }

    public interface IForce : IScope<BodyIdentity, NBodyStorage>
    {
        IGravityForce Gravity => GetScope<IGravityForce>();
        Vector Force => Gravity.Force;

    }

    public interface IGravityForce : IScope<BodyIdentity,NBodyStorage>
    {
        /// <summary>
        /// Gravity Constant
        /// </summary>
        public const double G = 6.6743e-11;
        public const double MinusThreeHalf = -3.0 / 2.0;

        Vector Force => Enumerable.Range(0, GetStorage().NBodies)
                                         .Where(i => i != Identity.Id)
                                         .Select(i =>
                                                 {
                                                     var me = GetScope<IBody>(Identity).Previous;
                                                     var other = GetScope<IBody>(Identity with { Id = i }).Previous;
                                                     var distance = other.Position - me.Position;
                                                     // Gravity law: G*m1*m2/r^2
                                                     // Vector is not normalized ==> G*m1*m2/r^3
                                                     // 1 / r^3 = r ^-3 = r^2^(-3/2)
                                                     var oneOverRToThe3 = Math.Pow(distance.LengthSquared, MinusThreeHalf);
                                                     var gravityScale = G * me.Mass * other.Mass * oneOverRToThe3;
                                                     return gravityScale * distance;
                                                 })
                                         .Aggregate();
    }

    public class NBodyStorage
    {
        private readonly Random rng = new(20210908);

        public NBodyStorage(int nBodies, double deltaT)
        {
            NBodies = nBodies;
            DeltaT = deltaT;
        }

        public IEnumerable<BodyIdentity> Identities(int timeIndex) => Enumerable.Range(0, NBodies).Select(i => new BodyIdentity(i, timeIndex));

        public int NBodies { get; }
        public double DeltaT { get; }

        public Vector GetPosition(BodyIdentity identity)
        {
            return new(GetRandomCoordinate(), GetRandomCoordinate());
        }

        public double GetRandomCoordinate() => rng.NextDouble() * 2 - 1;

        public Vector GetVelocity(BodyIdentity identity)
        {
            return new(GetRandomCoordinate(), GetRandomCoordinate());
        }

        public double GetMass(BodyIdentity identity) => 1;
    }

    public class SimulationState
    {
        public ICollection<IBody> Current { get; set; }
        public ICollection<IBody> Previous { get; set; }
    }

    public static class SimulationExtensions
    {
        /// <summary>
        /// This extension is used to run the simulation.
        /// This is used for unit testing only. 
        /// </summary>
        /// <param name="state"></param>
        /// <param name="stream"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public static Task StartSimulation(this SimulationState state, IObserver<Vector[]> stream, CancellationToken cancellationToken)
        {
            while (true)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                stream.OnNext(state.Current.Select(x => x.Position).ToArray());

                if(state.Previous != null)
                    foreach (var prev in state.Previous)
                        prev.Dispose();
                
                state.Previous = state.Current;
                state.Current = state.Current.Select(b => b.GetScope<IBody>(b.Identity with { TimeIndex = b.Identity.TimeIndex + 1 })).ToArray();
            }

            return Task.CompletedTask;
        }
    }
}
