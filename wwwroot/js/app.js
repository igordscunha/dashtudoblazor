// Interop JS do DashTudo: gráficos (Chart.js) e downloads.
window.dashtudo = (() => {
  const charts = new WeakMap();
  const palette = ['#5B63B7', '#EF4444', '#10B981', '#F59E0B', '#8B5CF6', '#F97316', '#06B6D4', '#EC4899', '#84CC16', '#64748B'];
  const fmt = new Intl.NumberFormat('pt-BR', { maximumFractionDigits: 2 });

  function buildConfig(spec) {
    const circular = spec.kind === 'pie' || spec.kind === 'doughnut';
    const type = circular ? spec.kind : (spec.kind === 'line' || spec.kind === 'area') ? 'line' : 'bar';

    const datasets = spec.series.map((s, i) => {
      const color = palette[i % palette.length];
      return {
        label: s.label,
        data: s.values,
        backgroundColor: circular ? spec.labels.map((_, j) => palette[j % palette.length]) : (type === 'line' ? color + '33' : color),
        borderColor: circular ? '#fff' : color,
        borderWidth: type === 'line' ? 2 : 1,
        fill: spec.kind === 'area',
        tension: 0.25,
        pointRadius: spec.labels.length > 60 ? 0 : 3,
      };
    });

    return {
      type,
      data: { labels: spec.labels, datasets },
      options: {
        responsive: true,
        maintainAspectRatio: false,
        animation: { duration: 300 },
        indexAxis: spec.kind === 'horizontalBar' ? 'y' : 'x',
        plugins: {
          title: { display: !!spec.title, text: spec.title, font: { size: 16, weight: 'bold' } },
          legend: { display: circular || datasets.length > 1, position: 'top' },
          tooltip: {
            callbacks: {
              label: ctx => {
                const v = circular ? ctx.parsed : (spec.kind === 'horizontalBar' ? ctx.parsed.x : ctx.parsed.y);
                return `${ctx.dataset.label}: ${fmt.format(v)}`;
              },
            },
          },
        },
        scales: circular ? {} : {
          x: { title: { display: !!spec.xLabel, text: spec.kind === 'horizontalBar' ? spec.yLabel : spec.xLabel }, ticks: { autoSkip: true, maxRotation: 60 } },
          y: { beginAtZero: true, title: { display: !!spec.yLabel, text: spec.kind === 'horizontalBar' ? spec.xLabel : spec.yLabel }, ticks: { callback: v => typeof v === 'number' ? fmt.format(v) : v } },
        },
      },
      // Fundo branco para o PNG exportado não ficar transparente.
      plugins: [{
        id: 'whiteBg',
        beforeDraw: chart => {
          const { ctx } = chart;
          ctx.save();
          ctx.globalCompositeOperation = 'destination-over';
          ctx.fillStyle = '#ffffff';
          ctx.fillRect(0, 0, chart.width, chart.height);
          ctx.restore();
        },
      }],
    };
  }

  return {
    renderChart(canvas, spec) {
      if (!canvas || !window.Chart) return;
      charts.get(canvas)?.destroy();
      charts.set(canvas, new Chart(canvas, buildConfig(spec)));
    },
    destroyChart(canvas) {
      if (!canvas) return;
      charts.get(canvas)?.destroy();
      charts.delete(canvas);
    },
    exportChart(canvas, fileName) {
      const chart = charts.get(canvas);
      if (!chart) return;
      const a = document.createElement('a');
      a.download = fileName;
      a.href = chart.toBase64Image('image/png', 1);
      a.click();
    },
    downloadText(fileName, content, mime) {
      const blob = new Blob([content], { type: mime || 'text/plain;charset=utf-8' });
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.download = fileName;
      a.href = url;
      a.click();
      setTimeout(() => URL.revokeObjectURL(url), 1000);
    },
    async copyText(text) {
      try { await navigator.clipboard.writeText(text); return true; } catch { return false; }
    },
    print() { window.print(); },
  };
})();
