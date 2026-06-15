//! The vehicle propulsion model the guidance algorithms consume — thrust bounds, exhaust velocity,
//! and the derived quantities (mass-flow-per-Newton α, the min/max thrust ρ₁/ρ₂) used by G-FOLD's
//! convexification. Rebuilt on staging (the live values come from `/sim` engine fields). Plain SI.

/// Standard gravity used for Isp (`ve = Isp·g₀`) — matches KSA (`9.80665`).
pub const G0: f64 = 9.80665;

/// A point-mass rocket: total mass = dry + propellant, a vacuum thrust range, and an exhaust velocity.
#[derive(Debug, Clone, Copy)]
pub struct VehicleModel {
    /// Dry (empty) mass, kg.
    pub m_dry: f64,
    /// Usable propellant mass, kg.
    pub m_fuel: f64,
    /// Specific impulse, s (vacuum).
    pub isp: f64,
    /// Max thrust at full throttle, N.
    pub thrust_max: f64,
    /// Minimum throttle fraction (deep-throttle floor), 0..1.
    pub throttle_min: f64,
    /// Maximum throttle fraction, 0..1.
    pub throttle_max: f64,
}

impl VehicleModel {
    /// Wet mass (dry + propellant), kg.
    pub fn m_wet(&self) -> f64 {
        self.m_dry + self.m_fuel
    }

    /// `ln(m_wet)` — the initial value of the log-mass state `z`.
    pub fn m_wet_log(&self) -> f64 {
        self.m_wet().ln()
    }

    /// Mass-flow per unit thrust, `α = 1/(g₀·Isp)` (kg/s per N). `ż = −α·σ` in the convex program.
    pub fn alpha(&self) -> f64 {
        1.0 / (G0 * self.isp)
    }

    /// Lower thrust bound `ρ₁ = throttle_min · thrust_max`, N.
    pub fn rho1(&self) -> f64 {
        self.throttle_min * self.thrust_max
    }

    /// Upper thrust bound `ρ₂ = throttle_max · thrust_max`, N.
    pub fn rho2(&self) -> f64 {
        self.throttle_max * self.thrust_max
    }
}
