using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AsistenteJuridico.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Fase 6.2: ai_usage_logs.ConversationId queda como referencia histórica sin FK hacia ai_conversations,
    /// de modo que la purga física de conversaciones no requiera UPDATE/DELETE sobre el registro inmutable.
    /// </summary>
    public partial class Fase62DropAIUsageLogConversationForeignKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // IF EXISTS: en bases donde la restricción ya se eliminó manualmente la migración no falla;
            // en una base creada desde cero elimina la FK creada por AddFase6AILegalAssistant.
            migrationBuilder.Sql(@"
ALTER TABLE ai_usage_logs
    DROP CONSTRAINT IF EXISTS ""FK_ai_usage_logs_ai_conversations_TenantId_ConversationId"";
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // NOT VALID: tras una purga pueden existir ConversationId históricos sin conversación;
            // la restricción se restaura solo para filas nuevas sin tocar el registro inmutable.
            migrationBuilder.Sql(@"
ALTER TABLE ai_usage_logs
    ADD CONSTRAINT ""FK_ai_usage_logs_ai_conversations_TenantId_ConversationId""
    FOREIGN KEY (""TenantId"", ""ConversationId"")
    REFERENCES ai_conversations (""TenantId"", ""Id"")
    NOT VALID;
");
        }
    }
}
