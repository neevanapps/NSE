// Thin interop layer over Chart.js (loaded via CDN in App.razor). Keeps one Chart
// instance per canvas id so live updates mutate in place instead of destroying and
// recreating the chart on every tick (avoids flicker / re-animation on each refresh).

const charts = {};

function baseGridOptions() {
    return {
        grid: { color: "rgba(255,255,255,0.06)" },
        ticks: { color: "rgba(255,255,255,0.55)", font: { size: 11 } },
    };
}

// Labels are pre-formatted "HH:mm:ss" strings on a category axis, deliberately not a Chart.js
// `time` scale -- App.razor loads the plain UMD bundle with no date adapter, and a time scale
// without one fails silently rather than erroring. maxTicksLimit keeps ~120 points readable.
function timeAxisOptions() {
    return {
        ...baseGridOptions(),
        ticks: { ...baseGridOptions().ticks, maxTicksLimit: 8, maxRotation: 0, autoSkip: true },
    };
}

export function renderLineChart(canvasId, labels, data, color) {
    const ctx = document.getElementById(canvasId);
    if (!ctx) return;

    if (charts[canvasId]) {
        charts[canvasId].destroy();
    }

    charts[canvasId] = new Chart(ctx, {
        type: "line",
        data: {
            labels: labels,
            datasets: [{
                data: data,
                borderColor: color,
                backgroundColor: color + "22",
                borderWidth: 2,
                pointRadius: 0,
                tension: 0.3,
                fill: true,
            }],
        },
        options: {
            animation: { duration: 300 },
            responsive: true,
            maintainAspectRatio: false,
            plugins: { legend: { display: false } },
            scales: {
                x: { ...timeAxisOptions() },
                y: { ...baseGridOptions() },
            },
        },
    });
}

// Score, its fast (10m)/slow (30m) trailing reads, and spot price on one time axis. Score/fast/
// slow share one y-axis (all the same -100..+100 scale); price gets its own on the right (wildly
// different numeric range, ~24,000). Reading fast vs slow together is the point -- their
// crossover is exactly what the Crossover strategy trades on, and seeing it alongside the instant
// score (which the Hysteresis strategy trades on) and spot price turns "did the score lead or lag
// price, and is a flip coming" into something visible at a glance instead of a manual query.
export function renderCoreScoreChart(canvasId, labels, scoreData, fastData, slowData, priceData) {
    const ctx = document.getElementById(canvasId);
    if (!ctx) return;

    if (charts[canvasId]) {
        charts[canvasId].destroy();
    }

    charts[canvasId] = new Chart(ctx, {
        type: "line",
        data: {
            labels: labels,
            datasets: [
                {
                    label: "Score",
                    data: scoreData,
                    borderColor: "#38bdf8",
                    backgroundColor: "#38bdf822",
                    borderWidth: 2,
                    pointRadius: 0,
                    tension: 0.3,
                    fill: true,
                    yAxisID: "score",
                },
                {
                    label: "Fast (10m)",
                    data: fastData,
                    borderColor: "#a78bfa",
                    borderWidth: 1.5,
                    pointRadius: 0,
                    tension: 0.3,
                    fill: false,
                    borderDash: [2, 2],
                    yAxisID: "score",
                },
                {
                    label: "Slow (30m)",
                    data: slowData,
                    borderColor: "#2dd4bf",
                    borderWidth: 1.5,
                    pointRadius: 0,
                    tension: 0.3,
                    fill: false,
                    borderDash: [7, 3],
                    yAxisID: "score",
                },
                {
                    label: "Spot",
                    data: priceData,
                    borderColor: "#fbbf24",
                    borderWidth: 1.5,
                    pointRadius: 0,
                    tension: 0.3,
                    fill: false,
                    borderDash: [4, 3],
                    yAxisID: "price",
                },
            ],
        },
        options: {
            animation: { duration: 300 },
            responsive: true,
            maintainAspectRatio: false,
            plugins: {
                legend: {
                    display: true,
                    position: "top",
                    align: "end",
                    labels: { color: "rgba(255,255,255,0.55)", boxWidth: 10, boxHeight: 2, font: { size: 10 } },
                },
            },
            scales: {
                x: { ...timeAxisOptions() },
                score: { ...baseGridOptions(), position: "left" },
                // Price gets no gridlines of its own -- two overlapping grids on one plot is
                // visual noise; the score axis is the reference.
                price: { ...baseGridOptions(), position: "right", grid: { display: false } },
            },
        },
    });
}

export function updateCoreScoreChart(canvasId, labels, scoreData, fastData, slowData, priceData) {
    const chart = charts[canvasId];
    if (!chart) return;
    chart.data.labels = labels;
    chart.data.datasets[0].data = scoreData;
    chart.data.datasets[1].data = fastData;
    chart.data.datasets[2].data = slowData;
    chart.data.datasets[3].data = priceData;
    chart.update("none");
}

// OptionsScoreThreeWaySwitchMaxPainConfirmed has no fast/slow MA concept (it's a session-gated
// percentile switch, not a moving-average crossover) -- reusing renderCoreScoreChart's own
// "Fast"/"Slow" labels for it would mislabel the lines, so this is its own function with the
// series that actually apply: the traded ScaledScore, its Percentile (0..100, own axis --
// deliberately not scaled onto the -100..100 score axis since a percentile above/below 90/10 is
// what actually gates entry, and squashing it onto the score axis would hide that read), the
// Max Pain confirmation score (-1..1, scaled by 100 to share the score axis, since it's compared
// against the same 0-crossing convention the score's own sign uses), and Price.
export function renderOptionsScoreChart(canvasId, labels, scoreData, percentileData, maxPainData, priceData) {
    const ctx = document.getElementById(canvasId);
    if (!ctx) return;

    if (charts[canvasId]) {
        charts[canvasId].destroy();
    }

    charts[canvasId] = new Chart(ctx, {
        type: "line",
        data: {
            labels: labels,
            datasets: [
                {
                    label: "Score",
                    data: scoreData,
                    borderColor: "#38bdf8",
                    backgroundColor: "#38bdf822",
                    borderWidth: 2,
                    pointRadius: 0,
                    tension: 0.3,
                    fill: true,
                    yAxisID: "score",
                },
                {
                    label: "Max Pain confirm x100",
                    data: maxPainData,
                    borderColor: "#f472b6",
                    borderWidth: 1.5,
                    pointRadius: 0,
                    tension: 0.3,
                    fill: false,
                    borderDash: [2, 2],
                    yAxisID: "score",
                },
                {
                    label: "Percentile",
                    data: percentileData,
                    borderColor: "#a78bfa",
                    borderWidth: 1.5,
                    pointRadius: 0,
                    tension: 0.3,
                    fill: false,
                    borderDash: [7, 3],
                    yAxisID: "percentile",
                },
                {
                    label: "Price",
                    data: priceData,
                    borderColor: "#fbbf24",
                    borderWidth: 1.5,
                    pointRadius: 0,
                    tension: 0.3,
                    fill: false,
                    borderDash: [4, 3],
                    yAxisID: "price",
                },
            ],
        },
        options: {
            animation: { duration: 300 },
            responsive: true,
            maintainAspectRatio: false,
            plugins: {
                legend: {
                    display: true,
                    position: "top",
                    align: "end",
                    labels: { color: "rgba(255,255,255,0.55)", boxWidth: 10, boxHeight: 2, font: { size: 10 } },
                },
            },
            scales: {
                x: { ...timeAxisOptions() },
                score: { ...baseGridOptions(), position: "left", min: -100, max: 100 },
                // 0..100, its own axis -- a percentile isn't on the same scale as the -100..100
                // score, and squashing it in would flatten exactly the 90/10 extremes that matter.
                percentile: { ...baseGridOptions(), position: "left", min: 0, max: 100, display: false },
                price: { ...baseGridOptions(), position: "right", grid: { display: false } },
            },
        },
    });
}

export function updateOptionsScoreChart(canvasId, labels, scoreData, percentileData, maxPainData, priceData) {
    const chart = charts[canvasId];
    if (!chart) return;
    chart.data.labels = labels;
    chart.data.datasets[0].data = scoreData;
    chart.data.datasets[1].data = maxPainData;
    chart.data.datasets[2].data = percentileData;
    chart.data.datasets[3].data = priceData;
    chart.update("none");
}

export function updateLineChart(canvasId, labels, data) {
    const chart = charts[canvasId];
    if (!chart) return;
    chart.data.labels = labels;
    chart.data.datasets[0].data = data;
    chart.update("none");
}

export function renderBarChart(canvasId, labels, data, colors) {
    const ctx = document.getElementById(canvasId);
    if (!ctx) return;

    if (charts[canvasId]) {
        charts[canvasId].destroy();
    }

    charts[canvasId] = new Chart(ctx, {
        type: "bar",
        data: {
            labels: labels,
            datasets: [{ data: data, backgroundColor: colors, borderRadius: 4 }],
        },
        options: {
            animation: { duration: 300 },
            responsive: true,
            maintainAspectRatio: false,
            plugins: { legend: { display: false } },
            indexAxis: "y",
            scales: {
                x: { ...baseGridOptions() },
                y: { ...baseGridOptions(), grid: { display: false } },
            },
        },
    });
}

export function updateBarChart(canvasId, labels, data, colors) {
    const chart = charts[canvasId];
    if (!chart) return;
    chart.data.labels = labels;
    chart.data.datasets[0].data = data;
    chart.data.datasets[0].backgroundColor = colors;
    chart.update("none");
}

// Two real series (e.g. call OI vs put OI) sharing one category axis, one positive and
// one negative so they mirror around zero -- the standard "OI profile" read. A single
// dataset can't show both sides at once, which is why this is separate from
// renderBarChart/updateBarChart above.
export function renderDivergingBarChart(canvasId, labels, positiveSeries, negativeSeries, positiveColor, negativeColor) {
    const ctx = document.getElementById(canvasId);
    if (!ctx) return;

    if (charts[canvasId]) {
        charts[canvasId].destroy();
    }

    charts[canvasId] = new Chart(ctx, {
        type: "bar",
        data: {
            labels: labels,
            datasets: [
                { label: "Call OI Δ (30m)", data: positiveSeries, backgroundColor: positiveColor, borderRadius: 3, stack: "oi" },
                { label: "Put OI Δ (30m)", data: negativeSeries, backgroundColor: negativeColor, borderRadius: 3, stack: "oi" },
            ],
        },
        options: {
            animation: { duration: 300 },
            responsive: true,
            maintainAspectRatio: false,
            plugins: { legend: { display: false } },
            indexAxis: "y",
            scales: {
                x: { ...baseGridOptions(), stacked: true },
                y: { ...baseGridOptions(), grid: { display: false }, stacked: true },
            },
        },
    });
}

export function updateDivergingBarChart(canvasId, labels, positiveSeries, negativeSeries) {
    const chart = charts[canvasId];
    if (!chart) return;
    chart.data.labels = labels;
    chart.data.datasets[0].data = positiveSeries;
    chart.data.datasets[1].data = negativeSeries;
    chart.update("none");
}
