import { Component, OnInit, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { AIService } from '../../core/services/ai.service';
import {
  AICasoUso,
  AIChatCompletionChunk,
  AIConversationDetailDto,
  AIConversationSummaryDto,
  AIMessageDto,
  AIRolMensaje
} from '../../core/models/ai.models';

@Component({
  selector: 'app-ai-chat',
  standalone: true,
  imports: [CommonModule, FormsModule],
  template: `
    <div class="ai-container">
      <aside class="ai-sidebar">
        <div class="sidebar-header">
          <h3>Asistente Jurídico IA</h3>
          <button class="btn-primary" (click)="nuevaConversacion()">+ Nueva Consulta</button>
        </div>
        <div class="conversations-list">
          <div *ngIf="cargandoConversaciones()" class="loading">Cargando conversaciones...</div>
          <div
            *ngFor="let c of conversaciones()"
            class="conversation-item"
            [class.active]="c.id === conversacionActiva()?.id"
            (click)="cargarConversacion(c.id)"
          >
            <div class="conv-title">{{ c.titulo }}</div>
            <div class="conv-meta">
              <span>{{ getCasoUsoLabel(c.casoUso) }}</span>
              <button class="btn-delete" (click)="eliminarConversacion(c.id, $event)">✕</button>
            </div>
          </div>
          <div *ngIf="conversaciones().length === 0 && !cargandoConversaciones()" class="empty-state">
            Sin conversaciones registradas
          </div>
        </div>
      </aside>

      <main class="ai-main">
        <div class="chat-header">
          <h4>{{ conversacionActiva()?.titulo || 'Nueva Consulta Jurídica' }}</h4>
          <span class="caso-badge" *ngIf="conversacionActiva()">
            {{ getCasoUsoLabel(conversacionActiva()!.casoUso) }}
          </span>
        </div>

        <div class="chat-messages" id="chatMessages">
          <div class="disclaimer-banner">
            ⚖️ <strong>Aviso Deontológico:</strong> Las respuestas de esta IA jurídica tienen carácter orientativo y de borrador.
            No sustituyen la verificación técnica ni la responsabilidad profesional del abogado.
          </div>

          <div
            *ngFor="let msg of mensajes()"
            class="message-bubble"
            [class.user-bubble]="msg.rol === RolMensaje.User"
            [class.assistant-bubble]="msg.rol === RolMensaje.Assistant"
          >
            <div class="bubble-header">
              <strong>{{ msg.rol === RolMensaje.User ? 'Tú' : 'Asistente Jurídico IA' }}</strong>
              <small>{{ msg.createdAt | date:'shortTime' }}</small>
            </div>
            <div class="bubble-content" [innerHTML]="formatearTexto(msg.contenido)"></div>
            <div *ngIf="msg.disclaimer" class="message-disclaimer">
              {{ msg.disclaimer }}
            </div>
          </div>

          <div *ngIf="streamingText()" class="message-bubble assistant-bubble streaming">
            <div class="bubble-header">
              <strong>Asistente Jurídico IA (Escribiendo...)</strong>
            </div>
            <div class="bubble-content" [innerHTML]="formatearTexto(streamingText())"></div>
          </div>
        </div>

        <div class="chat-input-area">
          <div *ngIf="procesando()" class="streaming-controls">
            <button class="btn-cancelar" (click)="cancelarGeneracion()">⏹ Detener generación</button>
          </div>
          <form (submit)="enviarMensaje($event)" class="input-form">
            <textarea
              [(ngModel)]="mensajeTexto"
              name="mensaje"
              placeholder="Escribe tu consulta jurídica, solicitud de análisis o instrucción procesal..."
              rows="2"
              [disabled]="procesando()"
              (keydown.enter)="onEnterPressed($event)"
            ></textarea>
            <button type="submit" class="btn-send" [disabled]="!mensajeTexto.trim() || procesando()">
              ➤ Enviar
            </button>
          </form>
        </div>
      </main>
    </div>
  `,
  styles: [`
    .ai-container {
      display: flex;
      height: calc(100vh - 80px);
      background-color: #f8fafc;
      font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif;
    }
    .ai-sidebar {
      width: 300px;
      border-right: 1px solid #e2e8f0;
      background: #ffffff;
      display: flex;
      flex-direction: column;
    }
    .sidebar-header {
      padding: 16px;
      border-bottom: 1px solid #e2e8f0;
    }
    .sidebar-header h3 {
      margin: 0 0 12px 0;
      font-size: 1.1rem;
      color: #1e293b;
    }
    .btn-primary {
      width: 100%;
      padding: 8px 12px;
      background: #2563eb;
      color: #fff;
      border: none;
      border-radius: 6px;
      cursor: pointer;
      font-weight: 500;
    }
    .btn-primary:hover { background: #1d4ed8; }
    .conversations-list {
      flex: 1;
      overflow-y: auto;
      padding: 8px;
    }
    .conversation-item {
      padding: 10px 12px;
      border-radius: 6px;
      margin-bottom: 4px;
      cursor: pointer;
      transition: background 0.15s;
    }
    .conversation-item:hover { background: #f1f5f9; }
    .conversation-item.active { background: #e0e7ff; color: #1e1b4b; }
    .conv-title {
      font-weight: 500;
      font-size: 0.9rem;
      white-space: nowrap;
      overflow: hidden;
      text-overflow: ellipsis;
    }
    .conv-meta {
      display: flex;
      justify-content: space-between;
      align-items: center;
      margin-top: 4px;
      font-size: 0.75rem;
      color: #64748b;
    }
    .btn-delete {
      background: none;
      border: none;
      color: #94a3b8;
      cursor: pointer;
      font-size: 0.8rem;
    }
    .btn-delete:hover { color: #ef4444; }
    .ai-main {
      flex: 1;
      display: flex;
      flex-direction: column;
    }
    .chat-header {
      padding: 16px 24px;
      border-bottom: 1px solid #e2e8f0;
      background: #fff;
      display: flex;
      justify-content: space-between;
      align-items: center;
    }
    .chat-header h4 { margin: 0; font-size: 1rem; color: #0f172a; }
    .caso-badge {
      font-size: 0.75rem;
      padding: 4px 8px;
      background: #f1f5f9;
      color: #475569;
      border-radius: 12px;
    }
    .chat-messages {
      flex: 1;
      overflow-y: auto;
      padding: 24px;
      display: flex;
      flex-direction: column;
      gap: 16px;
    }
    .disclaimer-banner {
      background: #fef3c7;
      border: 1px solid #fde68a;
      border-radius: 8px;
      padding: 12px 16px;
      font-size: 0.85rem;
      color: #92400e;
    }
    .message-bubble {
      max-width: 80%;
      padding: 14px 18px;
      border-radius: 10px;
      line-height: 1.5;
      font-size: 0.95rem;
    }
    .user-bubble {
      align-self: flex-end;
      background: #2563eb;
      color: #ffffff;
      border-bottom-right-radius: 2px;
    }
    .assistant-bubble {
      align-self: flex-start;
      background: #ffffff;
      color: #1e293b;
      border: 1px solid #e2e8f0;
      border-bottom-left-radius: 2px;
    }
    .bubble-header {
      display: flex;
      justify-content: space-between;
      margin-bottom: 6px;
      font-size: 0.8rem;
      opacity: 0.85;
    }
    .message-disclaimer {
      margin-top: 8px;
      padding-top: 8px;
      border-top: 1px solid #e2e8f0;
      font-size: 0.75rem;
      color: #64748b;
      font-style: italic;
    }
    .chat-input-area {
      padding: 16px 24px;
      background: #ffffff;
      border-top: 1px solid #e2e8f0;
    }
    .streaming-controls {
      margin-bottom: 8px;
      text-align: right;
    }
    .btn-cancelar {
      padding: 6px 12px;
      background: #fee2e2;
      color: #dc2626;
      border: 1px solid #fca5a5;
      border-radius: 6px;
      font-size: 0.85rem;
      cursor: pointer;
    }
    .btn-cancelar:hover { background: #fecaca; }
    .input-form {
      display: flex;
      gap: 12px;
      align-items: flex-end;
    }
    textarea {
      flex: 1;
      padding: 10px 14px;
      border: 1px solid #cbd5e1;
      border-radius: 8px;
      resize: none;
      font-family: inherit;
      font-size: 0.95rem;
    }
    textarea:focus { outline: none; border-color: #2563eb; }
    .btn-send {
      padding: 12px 20px;
      background: #2563eb;
      color: white;
      border: none;
      border-radius: 8px;
      font-weight: 500;
      cursor: pointer;
    }
    .btn-send:disabled { background: #94a3b8; cursor: not-allowed; }
  `]
})
export class AIChatComponent implements OnInit {
  private readonly aiService = inject(AIService);

  readonly RolMensaje = AIRolMensaje;
  readonly conversaciones = signal<AIConversationSummaryDto[]>([]);
  readonly conversacionActiva = signal<AIConversationDetailDto | null>(null);
  readonly mensajes = signal<AIMessageDto[]>([]);
  readonly streamingText = signal<string>('');
  readonly procesando = signal<boolean>(false);
  readonly cargandoConversaciones = signal<boolean>(false);

  mensajeTexto = '';
  private currentAbortController: AbortController | null = null;

  ngOnInit(): void {
    this.cargarConversaciones();
  }

  cargarConversaciones(): void {
    this.cargandoConversaciones.set(true);
    this.aiService.getConversaciones().subscribe({
      next: (res) => {
        if (res.success && res.data) {
          this.conversaciones.set(res.data);
        }
        this.cargandoConversaciones.set(false);
      },
      error: () => this.cargandoConversaciones.set(false)
    });
  }

  cargarConversacion(id: string): void {
    this.aiService.getConversacion(id).subscribe({
      next: (res) => {
        if (res.success && res.data) {
          this.conversacionActiva.set(res.data);
          this.mensajes.set(res.data.mensajes);
        }
      }
    });
  }

  nuevaConversacion(): void {
    this.conversacionActiva.set(null);
    this.mensajes.set([]);
    this.streamingText.set('');
  }

  eliminarConversacion(id: string, event: Event): void {
    event.stopPropagation();
    if (!confirm('¿Confirma que desea eliminar esta conversación?')) return;

    this.aiService.deleteConversacion(id).subscribe({
      next: () => {
        this.conversaciones.update((list) => list.filter((c) => c.id !== id));
        if (this.conversacionActiva()?.id === id) {
          this.nuevaConversacion();
        }
      }
    });
  }

  async enviarMensaje(event?: Event): Promise<void> {
    if (event) event.preventDefault();
    const texto = this.mensajeTexto.trim();
    if (!texto || this.procesando()) return;

    this.mensajeTexto = '';
    this.procesando.set(true);
    this.streamingText.set('');

    const convId = this.conversacionActiva()?.id;
    const nuevoMensajeUsuario: AIMessageDto = {
      id: 'temp-' + Date.now(),
      rol: AIRolMensaje.User,
      contenido: texto,
      createdAt: new Date().toISOString(),
      disclaimer: '',
      contextoAutorizado: false
    };

    this.mensajes.update((prev) => [...prev, nuevoMensajeUsuario]);

    this.currentAbortController = new AbortController();

    try {
      await this.aiService.streamMessage(
        {
          conversationId: convId,
          mensaje: texto,
          streaming: true
        },
        (chunk: AIChatCompletionChunk) => {
          if (chunk.deltaContent) {
            this.streamingText.update((curr) => curr + chunk.deltaContent);
          }
        },
        this.currentAbortController.signal
      );

      // Finalizó streaming
      if (this.streamingText()) {
        const respuestaAsistente: AIMessageDto = {
          id: 'temp-ast-' + Date.now(),
          rol: AIRolMensaje.Assistant,
          contenido: this.streamingText(),
          createdAt: new Date().toISOString(),
          disclaimer: 'AVISO DEONTOLÓGICO: La presente asistencia es orientativa y no vinculante.',
          contextoAutorizado: !!this.conversacionActiva()?.expedienteId
        };
        this.mensajes.update((prev) => [...prev, respuestaAsistente]);
      }
      this.streamingText.set('');
      this.cargarConversaciones();
    } catch (err: unknown) {
      if ((err as Error)?.name === 'AbortError') {
        console.log('Generación cancelada por el usuario.');
      } else {
        // Una respuesta interrumpida no se presenta como si estuviera completa
        this.streamingText.set('');
        console.error('Error al generar respuesta:', err);
      }
    } finally {
      this.procesando.set(false);
      this.currentAbortController = null;
    }
  }

  cancelarGeneracion(): void {
    if (this.currentAbortController) {
      this.currentAbortController.abort();
    }
  }

  onEnterPressed(event: Event): void {
    const kbEvent = event as KeyboardEvent;
    if (!kbEvent.shiftKey) {
      kbEvent.preventDefault();
      this.enviarMensaje();
    }
  }

  getCasoUsoLabel(casoUso: AICasoUso): string {
    switch (casoUso) {
      case AICasoUso.ResumenExpediente:
        return 'Resumen Expediente';
      case AICasoUso.ExtraccionHechos:
        return 'Extracción Hechos';
      case AICasoUso.RedaccionEscrito:
        return 'Redacción Escrito';
      case AICasoUso.ChatLibre:
      default:
        return 'Consulta Libre';
    }
  }

  formatearTexto(texto: string): string {
    if (!texto) return '';
    return texto.replace(/\n/g, '<br/>');
  }
}
