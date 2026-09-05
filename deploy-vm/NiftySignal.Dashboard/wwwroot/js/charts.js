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
                x: { display: false },
                y: { ...baseGridOptions() },
            },
        },
    });
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
                { label: "Call OI", data: positiveSeries, backgroundColor: positiveColor, borderRadius: 3, stack: "oi" },
                { label: "Put OI", data: negativeSeries, backgroundColor: negativeColor, borderRadius: 3, stack: "oi" },
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
