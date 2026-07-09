// disney_bsdf.slang
// Shared library for evaluating the full Disney BSDF including multiple scattering approx.

static const float PI = 3.14159265359;

// Utilities
float sqr(float x) { return x * x; }

// Schlick Fresnel
float3 F_Schlick(float cosTheta, float3 F0) {
    return F0 + (1.0 - F0) * pow(clamp(1.0 - cosTheta, 0.0, 1.0), 5.0);
}

// GGX Normal Distribution Function (Anisotropic)
float D_GGX_Aniso(float dotNH, float dotNX, float dotNY, float ax, float ay) {
    float a2 = ax * ay;
    float d = sqr(dotNX / ax) + sqr(dotNY / ay) + sqr(dotNH);
    return 1.0 / (PI * a2 * sqr(d) + 1e-5);
}

// Smith GGX Masking-Shadowing (Isotropic)
float G_SmithGGX(float dotNV, float dotNL, float alpha) {
    float a2 = alpha * alpha;
    float gv = dotNL * sqrt(a2 + (1.0 - a2) * dotNV * dotNV);
    float gl = dotNV * sqrt(a2 + (1.0 - a2) * dotNL * dotNL);
    return 0.5 / (gv + gl + 1e-5);
}

// GTR1 (Clearcoat NDF)
float D_GTR1(float dotNH, float alpha) {
    if (alpha >= 1.0) return 1.0 / PI;
    float a2 = alpha * alpha;
    float t = 1.0 + (a2 - 1.0) * dotNH * dotNH;
    return (a2 - 1.0) / (PI * log(a2) * t + 1e-5);
}

// Smith GGX Masking for Clearcoat (fixed alpha = 0.25)
float G_SmithGGX_Clearcoat(float dotNV, float dotNL) {
    float a2 = 0.25 * 0.25;
    float gv = dotNL * sqrt(a2 + (1.0 - a2) * dotNV * dotNV);
    float gl = dotNV * sqrt(a2 + (1.0 - a2) * dotNL * dotNL);
    return 0.5 / (gv + gl + 1e-5);
}

// Disney Diffuse & Subsurface
float3 DisneyDiffuse(float3 baseColor, float roughness, float dotNV, float dotNL, float dotLH, float subsurface) {
    float FL = pow(1.0 - dotNL, 5.0);
    float FV = pow(1.0 - dotNV, 5.0);
    
    // Base diffuse
    float Fd90 = 0.5 + 2.0 * dotLH * dotLH * roughness;
    float Fd = lerp(1.0, Fd90, FL) * lerp(1.0, Fd90, FV);
    
    // Subsurface approximation (Hanrahan-Krueger)
    float Fss90 = dotLH * dotLH * roughness;
    float Fss = lerp(1.0, Fss90, FL) * lerp(1.0, Fss90, FV);
    float ss = 1.25 * (Fss * (1.0 / (dotNL + dotNV + 1e-5) - 0.5) + 0.5);
    
    return baseColor / PI * lerp(Fd, ss, subsurface) * (1.0 - subsurface);
}

// Multiple Scattering Approximation (Kulla-Conty energy compensation)
float3 MultiScatterApprox(float3 F0, float roughness, float dotNV, float dotNL) {
    // A simplified directional albedo fit for GGX
    float albedo = lerp(1.0, 0.0, roughness);
    float energyLoss = 1.0 - albedo;
    
    // Distribute lost energy uniformly (Lambertian multiple bounce lobe)
    // Scaled heavily by roughness and metalness
    return F0 * (energyLoss * roughness * roughness) / PI;
}

struct DisneyMaterial {
    float3 baseColor;
    float metallic;
    float subsurface;
    float specular;
    float roughness;
    float specularTint;
    float anisotropic;
    float sheen;
    float sheenTint;
    float clearcoat;
    float clearcoatGloss;
};

// Master Evaluate Function
float3 EvaluateDisneyBSDF(DisneyMaterial mat, float3 N, float3 V, float3 L, float3 X, float3 Y) {
    float3 H = normalize(L + V);
    float dotNL = clamp(dot(N, L), 0.001, 1.0);
    float dotNV = clamp(dot(N, V), 0.001, 1.0);
    float dotNH = clamp(dot(N, H), 0.001, 1.0);
    float dotLH = clamp(dot(L, H), 0.001, 1.0);
    
    float luminance = dot(mat.baseColor, float3(0.3, 0.6, 0.1));
    float3 Ctint = luminance > 0.0 ? mat.baseColor / luminance : float3(1.0);
    float3 Cspec0 = lerp(mat.specular * 0.08 * lerp(float3(1.0), Ctint, mat.specularTint), mat.baseColor, mat.metallic);
    float3 Csheen = lerp(float3(1.0), Ctint, mat.sheenTint);
    
    // Diffuse
    float3 diffuse = DisneyDiffuse(mat.baseColor, mat.roughness, dotNV, dotNL, dotLH, mat.subsurface);
    diffuse *= (1.0 - mat.metallic); // Metals have no diffuse
    
    // Sheen
    float FH = pow(clamp(1.0 - dotLH, 0.0, 1.0), 5.0);
    float3 sheen = FH * mat.sheen * Csheen * (1.0 - mat.metallic);
    
    // Specular
    float aspect = sqrt(1.0 - mat.anisotropic * 0.9);
    float ax = max(0.001, sqr(mat.roughness) / aspect);
    float ay = max(0.001, sqr(mat.roughness) * aspect);
    float Ds = D_GGX_Aniso(dotNH, dot(X, H), dot(Y, H), ax, ay);
    float Gs = G_SmithGGX(dotNV, dotNL, mat.roughness);
    float3 Fs = F_Schlick(dotLH, Cspec0);
    float3 specular = (Ds * Gs * Fs); // (dotNL * dotNV) cancelled out by G_SmithGGX formula 0.5/(gv+gl)
    
    // Multi-scatter (Energy conservation for rough metals)
    float3 multiScatter = MultiScatterApprox(Cspec0, mat.roughness, dotNV, dotNL);
    specular += multiScatter * mat.metallic;
    
    // Clearcoat
    float Dr = D_GTR1(dotNH, lerp(0.1, 0.001, mat.clearcoatGloss));
    float Gr = G_SmithGGX_Clearcoat(dotNV, dotNL);
    float Fr = lerp(0.04, 1.0, FH);
    float3 clearcoat = mat.clearcoat * Dr * Gr * Fr;
    
    return diffuse + sheen + specular + clearcoat;
}

// =====================================================================================
// Sampling & PDF logic
// =====================================================================================

// Cosine weighted hemisphere sampling
float3 SampleCosineHemisphere(float u1, float u2, float3 N, float3 T, float3 B) {
    float r = sqrt(u1);
    float phi = 2.0 * PI * u2;
    float3 p = float3(r * cos(phi), r * sin(phi), sqrt(max(0.0, 1.0 - u1)));
    return normalize(p.x * T + p.y * B + p.z * N);
}

// GGX Importance Sampling (Isotropic for now)
float3 SampleGGX(float u1, float u2, float roughness, float3 N, float3 T, float3 B) {
    float a = roughness * roughness;
    float phi = 2.0 * PI * u1;
    float cosTheta = sqrt((1.0 - u2) / (1.0 + (a*a - 1.0) * u2));
    float sinTheta = sqrt(max(0.0, 1.0 - cosTheta * cosTheta));
    float3 H = float3(sinTheta * cos(phi), sinTheta * sin(phi), cosTheta);
    return normalize(H.x * T + H.y * B + H.z * N);
}

// PDF for Cosine Hemisphere
float PdfCosineHemisphere(float dotNL) {
    return dotNL / PI;
}

// PDF for GGX
float PdfGGX(float dotNH, float dotNH_V, float roughness) {
    float a = roughness * roughness;
    float d = dotNH * dotNH * (a * a - 1.0) + 1.0;
    float D = (a * a) / (PI * d * d);
    return (D * dotNH) / (4.0 * dotNH_V + 1e-5);
}

// Combined Disney Sample
// Returns the sampled direction L and sets the pdf.
float3 SampleDisneyBSDF(DisneyMaterial mat, float3 N, float3 V, float3 T, float3 B, float2 rnd, out float pdf) {
    float specularWeight = lerp(0.5, 1.0, mat.metallic); // Heuristic to choose between diffuse and specular
    
    if (rnd.x < specularWeight) {
        // Sample Specular
        rnd.x /= specularWeight;
        float3 H = SampleGGX(rnd.x, rnd.y, mat.roughness, N, T, B);
        float3 L = reflect(-V, H);
        
        float dotNL = clamp(dot(N, L), 0.001, 1.0);
        float dotNH = clamp(dot(N, H), 0.001, 1.0);
        float dotVH = clamp(dot(V, H), 0.001, 1.0);
        
        float pdfSpec = PdfGGX(dotNH, dotVH, mat.roughness);
        float pdfDiff = PdfCosineHemisphere(dotNL);
        
        pdf = lerp(pdfDiff, pdfSpec, specularWeight);
        return L;
    } else {
        // Sample Diffuse
        rnd.x = (rnd.x - specularWeight) / (1.0 - specularWeight);
        float3 L = SampleCosineHemisphere(rnd.x, rnd.y, N, T, B);
        
        float3 H = normalize(L + V);
        float dotNL = clamp(dot(N, L), 0.001, 1.0);
        float dotNH = clamp(dot(N, H), 0.001, 1.0);
        float dotVH = clamp(dot(V, H), 0.001, 1.0);
        
        float pdfSpec = PdfGGX(dotNH, dotVH, mat.roughness);
        float pdfDiff = PdfCosineHemisphere(dotNL);
        
        pdf = lerp(pdfDiff, pdfSpec, specularWeight);
        return L;
    }
}

float PdfDisneyBSDF(DisneyMaterial mat, float3 N, float3 V, float3 L) {
    float specularWeight = lerp(0.5, 1.0, mat.metallic);
    float3 H = normalize(L + V);
    float dotNL = clamp(dot(N, L), 0.001, 1.0);
    float dotNH = clamp(dot(N, H), 0.001, 1.0);
    float dotVH = clamp(dot(V, H), 0.001, 1.0);
    
    float pdfSpec = PdfGGX(dotNH, dotVH, mat.roughness);
    float pdfDiff = PdfCosineHemisphere(dotNL);
    
    return lerp(pdfDiff, pdfSpec, specularWeight);
}
