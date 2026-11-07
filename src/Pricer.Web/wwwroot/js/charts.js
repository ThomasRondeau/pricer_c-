// Chart.js glue for the LineChart component. Colours come from CSS variables so charts follow the page theme.
(function () {
    const palette = ["--series-1", "--series-2", "--series-3", "--series-4"];

    function css(name) {
        return getComputedStyle(document.documentElement).getPropertyValue(name).trim();
    }

    function datasets(spec) {
        return spec.series.map((s, i) => {
            const colour = css(palette[i % palette.length]);
            return {
                label: s.name,
                data: s.points,
                showLine: !s.markers,
                spanGaps: false,
                borderColor: colour,
                backgroundColor: colour,
                borderWidth: s.markers ? 0 : 2,
                pointRadius: s.markers ? 3.5 : 0,
                pointHoverRadius: s.markers ? 5 : 3,
            };
        });
    }

    function axis(title) {
        return {
            type: "linear",
            title: { display: title.length > 0, text: title, color: css("--text-muted") },
            ticks: { color: css("--text-muted") },
            grid: { color: css("--grid") },
        };
    }

    window.pricerCharts = {
        render(canvas, spec) {
            const existing = Chart.getChart(canvas);
            if (existing) {
                existing.data.datasets = datasets(spec);
                existing.options.plugins.title.text = spec.title;
                existing.options.plugins.legend.display = spec.series.length > 1;
                existing.update("none");
                return;
            }
            new Chart(canvas, {
                type: "scatter",
                data: { datasets: datasets(spec) },
                options: {
                    animation: false,
                    responsive: true,
                    maintainAspectRatio: false,
                    interaction: { mode: "nearest", intersect: false },
                    plugins: {
                        title: { display: spec.title.length > 0, text: spec.title, color: css("--text") },
                        legend: { display: spec.series.length > 1, labels: { color: css("--text") } },
                    },
                    scales: { x: axis(spec.xLabel), y: axis(spec.yLabel) },
                },
            });
        },
        dispose(canvas) {
            Chart.getChart(canvas)?.destroy();
        },
    };
})();
